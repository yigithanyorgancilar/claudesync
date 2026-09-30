using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudeSync;

public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? showSignal;

    /// <summary>Command line: --background (start in tray), --demo (sample data, no network),
    /// --screenshot &lt;png&gt; [--select &lt;n&gt;], --diagnostics &lt;txt&gt;.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;
        string? Arg(string name) => Array.IndexOf(args, name) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var demo = args.Contains("--demo") || Arg("--screenshot-window") is not null;
        var screenshot = Arg("--screenshot");

        if (!demo && screenshot is null)
        {
            instance = new Mutex(true, "ClaudeSyncDesktop-single-instance", out var created);
            if (!created)
            {
                try { using var existing = EventWaitHandle.OpenExisting("Local\\ClaudeSyncDesktopShow"); existing.Set(); } catch (Exception) { }
                Shutdown(); return;
            }
        }
        L.Use(demo ? Arg("--lang") : Store.Load<AppConfig>(Store.ConfigPath).Language);
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            Store.Log("Unhandled: " + ex.Exception);
            ex.Handled = true;
            if (MainWindow is { IsVisible: true } owner) Dialog.Info(owner, L.UnexpectedError, ex.Exception.Message, Tone.Error);
        };

        if (Arg("--diagnostics") is { } report) { _ = Diagnostics.Write(report).ContinueWith(_ => Dispatcher.Invoke(Shutdown)); return; }
        if (Arg("--screenshot-window") is { } kind && screenshot is not null) { PreviewWindow(kind, screenshot); return; }

        var window = new MainWindow(demo);
        MainWindow = window;
        if (!args.Contains("--background") || demo) window.Show();

        if (screenshot is not null)
        {
            if (int.TryParse(Arg("--select"), out var index)) window.SelectForPreview(index);
            window.ContentRendered += async (_, _) =>
            {
                await Task.Delay(1200);
                Capture(window, screenshot);
                window.ExitApplication();
            };
            return;
        }
        if (demo) return;

        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ClaudeSyncDesktopShow");
        var signal = showSignal;
        _ = Task.Run(() =>
        {
            while (signal.WaitOne() && !Dispatcher.HasShutdownStarted)
                Dispatcher.Invoke(window.ShowWindow);
        });
    }

    /// <summary>Design check for secondary windows: opens one with sample data, saves a PNG, exits.</summary>
    private void PreviewWindow(string kind, string path)
    {
        var sample = new LocalSession("local_1", Guid.NewGuid().ToString(), L.Turkish ? "Ürün planını oku" : "Read the product plan", @"C:\Users\you\Projects\movee", "", "x", DateTimeOffset.Now.AddHours(-2), false);
        Window window = kind switch
        {
            "settings" => new SettingsWindow(new AppConfig(), new LocalClaude(new AppConfig())),
            "welcome" => new MainWindow(false),
            "add" => new AddConversationWindow([sample, sample with { CliId = Guid.NewGuid().ToString(), Title = L.Turkish ? "Supabase edge function testleri" : "Supabase edge function tests" }]),
            "backups" => new BackupsWindow(null!, new ConversationItem("x") { Title = L.Turkish ? "Ürün planını oku" : "Read the product plan" }, null),
            _ => new Dialog(null, L.RemoveTitle, L.RemoveText(L.Turkish ? "Ürün planını oku" : "Read the product plan"), Tone.Warning, [L.RemoveFromSync, L.Cancel]),
        };
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ContentRendered += async (_, _) =>
        {
            await Task.Delay(800);
            Capture(window, path);
            Shutdown();
        };
        window.Show();
    }

    private static void Capture(Window window, string path)
    {
        var source = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var image = new RenderTargetBitmap((int)(source.ActualWidth * dpi.DpiScaleX), (int)(source.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen())
            context.DrawRectangle(window.Background is SolidColorBrush { Color.A: > 0 } b ? b : new SolidColorBrush(Color.FromRgb(243, 243, 243)), null,
                new Rect(0, 0, source.ActualWidth, source.ActualHeight));
        image.Render(background);
        image.Render(source);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        showSignal?.Set();
        instance?.Dispose();
        base.OnExit(e);
    }
}

internal static class Diagnostics
{
    public static async Task Write(string path)
    {
        var lines = new List<string> { "ClaudeSync diagnostics " + DateTimeOffset.Now.ToString("u") };
        try
        {
            var config = Store.Load<AppConfig>(Store.ConfigPath);
            var claude = new LocalClaude(config);
            lines.Add("Subscriptions: " + config.Subscriptions.Count + ", projects: " + config.Projects.Count);
            try
            {
                lines.Add("Claude data: " + claude.DesktopRoot());
                lines.Add("Account folder: " + claude.Group());
                lines.Add("Code conversations: " + claude.Sessions().Count);
            }
            catch (Exception ex) { lines.Add("Claude data: " + ex.Message); }
            foreach (var p in ClaudeProcesses.Snapshot().Processes)
                lines.Add($"Process {p.Id}: {p.Kind} {(p.SessionId is null ? "" : "session " + p.SessionId)} {p.Path}");
            var cloud = new CloudClient(config);
            lines.Add("Signed in: " + cloud.HasSession);
            if (cloud.HasSession)
            {
                var library = await cloud.Library();
                lines.Add("Cloud conversations: " + library["sessions"]?.AsArray().Count);
            }
        }
        catch (Exception ex) { lines.Add("Error: " + ex.Message); }
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }
}
