using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace ClaudeSync;

public partial class SettingsWindow : Window
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly (string? Value, string Label)[] Languages = [(null, L.LanguageAuto), ("en", "English"), ("tr", "Türkçe")];
    private readonly AppConfig config;

    public SettingsWindow(AppConfig config, LocalClaude claude)
    {
        InitializeComponent();
        this.config = config;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false)) Startup.IsChecked = key?.GetValue("ClaudeSync") is not null;
        LanguageBox.ItemsSource = Languages.Select(x => x.Label).ToArray();
        LanguageBox.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, x => x.Value == config.Language));
        DesktopRoot.Text = config.DesktopRoot ?? "";
        AccountId.Text = config.AccountId ?? "";
        WorkspaceId.Text = config.WorkspaceId ?? "";
        ProjectsRoot.Text = config.ProjectsRoot ?? "";
        SupabaseUrl.Text = config.SupabaseUrl;
        PublishableKey.Text = config.PublishableKey;
        WorkerUrl.Text = config.WorkerUrl;
        CopyCodeButton.IsEnabled = config.ServerConfigured;
        Device.Text = Environment.MachineName;
        DeviceId.Text = config.DeviceId.Length > 0 ? L.DeviceId(config.DeviceId) : L.NotRegistered;
        Version.Text = "ClaudeSync " + Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        try { Detected.Text = L.DetectedClaude(claude.Group(), claude.ProjectsRoot); }
        catch (Exception ex) { Detected.Text = L.ClaudeNotDetected + ex.Message; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var serverEmpty = new[] { SupabaseUrl.Text, PublishableKey.Text, WorkerUrl.Text }.All(string.IsNullOrWhiteSpace);
        if (!serverEmpty && (!AppConfig.IsHttps(SupabaseUrl.Text) || !AppConfig.IsHttps(WorkerUrl.Text) || string.IsNullOrWhiteSpace(PublishableKey.Text)))
        {
            Dialog.Info(this, L.InvalidServerTitle, L.InvalidServerText, Tone.Warning);
            return;
        }
        if ((Optional(AccountId.Text) is null) != (Optional(WorkspaceId.Text) is null))
        {
            Dialog.Info(this, L.FoldersIncomplete, L.FoldersIncompleteText, Tone.Warning);
            return;
        }
        config.SupabaseUrl = SupabaseUrl.Text.Trim().TrimEnd('/');
        config.PublishableKey = PublishableKey.Text.Trim();
        config.WorkerUrl = WorkerUrl.Text.Trim().TrimEnd('/');
        config.DesktopRoot = Optional(DesktopRoot.Text);
        config.AccountId = Optional(AccountId.Text);
        config.WorkspaceId = Optional(WorkspaceId.Text);
        config.ProjectsRoot = Optional(ProjectsRoot.Text);
        config.Language = Languages[Math.Max(0, LanguageBox.SelectedIndex)].Value;
        Store.Save(Store.ConfigPath, config);
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (Startup.IsChecked == true) key?.SetValue("ClaudeSync", $"\"{Environment.ProcessPath}\" --background");
            else key?.DeleteValue("ClaudeSync", false);
        }
        DialogResult = true;
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(config.ConnectionCode());
        CopyCodeButton.Content = L.Copied;
    }

    private void Source_Click(object sender, RoutedEventArgs e) => Links.Open(Links.Repository);
    private void Donate_Click(object sender, RoutedEventArgs e) => Links.Open(Links.Donate);

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Store.Home);
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        info.ArgumentList.Add(Store.Home);
        Process.Start(info);
    }
}
