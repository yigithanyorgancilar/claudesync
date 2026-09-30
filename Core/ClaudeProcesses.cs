using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeSync;

public enum ClaudeProcessKind
{
    /// <summary>The Claude Desktop app itself.</summary>
    Desktop,
    /// <summary>A Claude Code process Claude Desktop started for one of its Code tabs.</summary>
    DesktopSession,
    /// <summary>Claude Code started from a terminal.</summary>
    Cli,
    /// <summary>A claude.exe that is not a conversation (e.g. the Chrome native messaging host).</summary>
    Helper,
}

public sealed record ClaudeProcess(int Id, ClaudeProcessKind Kind, string Path, string? SessionId);

public sealed class ClaudeSnapshot(IReadOnlyList<ClaudeProcess> processes)
{
    public static readonly ClaudeSnapshot Empty = new([]);
    public IReadOnlyList<ClaudeProcess> Processes { get; } = processes;
    public bool DesktopRunning => Processes.Any(x => x.Kind == ClaudeProcessKind.Desktop);
    public string? DesktopPath => Processes.FirstOrDefault(x => x.Kind == ClaudeProcessKind.Desktop && x.Path.Length > 0)?.Path;
    /// <summary>True when some Claude process was started for this conversation.</summary>
    public bool IsOpen(string cliSessionId) => Processes.Any(x => string.Equals(x.SessionId, cliSessionId, StringComparison.OrdinalIgnoreCase));
    /// <summary>Terminal sessions whose conversation cannot be told from the command line (e.g. "claude --continue").</summary>
    public bool AnonymousCli => Processes.Any(x => x.Kind == ClaudeProcessKind.Cli && x.SessionId is null);
}

public static partial class ClaudeProcesses
{
    private static readonly string[] Names = ["claude", "claude-3p", "claude-code"];

    public static ClaudeSnapshot Snapshot()
    {
        var result = new List<ClaudeProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = process.ProcessName; } catch (InvalidOperationException) { continue; }
                if (!Names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                var (path, commandLine) = Inspect(process.Id);
                var kind = Classify(path, commandLine);
                result.Add(new(process.Id, kind, path, kind is ClaudeProcessKind.Cli or ClaudeProcessKind.DesktopSession ? SessionArgument(commandLine) : null));
            }
        }
        return new ClaudeSnapshot(result);
    }

    public static ClaudeProcessKind Classify(string path, string commandLine)
    {
        var p = path.Replace('/', '\\').ToLowerInvariant();
        if (commandLine.Contains("--chrome-native-host", StringComparison.OrdinalIgnoreCase)) return ClaudeProcessKind.Helper;
        if (p.Contains("\\claude-code\\") && (p.Contains("\\appdata\\roaming\\claude") || p.Contains("\\appdata\\local\\claude") || p.Contains("\\localcache\\roaming\\claude")))
            return ClaudeProcessKind.DesktopSession;
        if (p.Contains("\\windowsapps\\claude_") || p.Contains("\\programs\\claude\\") || p.Contains("\\anthropicclaude\\")
            || p.Contains("\\claude\\app-") || (p.Contains("\\claude-3p\\") && !p.Contains("\\claude-code\\")))
            return ClaudeProcessKind.Desktop;
        // Everything else, including processes we could not inspect, is treated as a terminal session.
        return ClaudeProcessKind.Cli;
    }

    [GeneratedRegex(@"(?:--resume|--session-id|-r)(?:=|\s+)""?(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})")]
    private static partial Regex SessionArg();
    public static string? SessionArgument(string commandLine) => SessionArg().Match(commandLine) is { Success: true } m ? m.Groups["id"].Value : null;

    /// <summary>Asks Claude Desktop to quit; returns its exe path so it can be reopened.</summary>
    public static async Task<string?> CloseDesktop(Func<bool> allowForce)
    {
        var snapshot = Snapshot();
        var desktop = snapshot.Processes.Where(x => x.Kind is ClaudeProcessKind.Desktop or ClaudeProcessKind.DesktopSession).ToArray();
        if (desktop.Length == 0) return null;
        var path = snapshot.DesktopPath;
        foreach (var item in desktop.Where(x => x.Kind == ClaudeProcessKind.Desktop))
        {
            try { using var process = Process.GetProcessById(item.Id); process.CloseMainWindow(); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        if (await WaitUntilClosed(TimeSpan.FromSeconds(8))) return path;
        if (!allowForce()) throw new OperationCanceledException(L.DesktopStayedOpen);
        foreach (var item in Snapshot().Processes.Where(x => x.Kind is ClaudeProcessKind.Desktop or ClaudeProcessKind.DesktopSession))
        {
            try { using var process = Process.GetProcessById(item.Id); process.Kill(entireProcessTree: true); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        if (await WaitUntilClosed(TimeSpan.FromSeconds(10))) return path;
        throw new InvalidOperationException(L.DesktopNotClosed);
    }

    private static async Task<bool> WaitUntilClosed(TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            if (!Snapshot().Processes.Any(x => x.Kind is ClaudeProcessKind.Desktop or ClaudeProcessKind.DesktopSession)) return true;
            await Task.Delay(400);
        }
        return false;
    }

    public static void Reopen(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Snapshot().DesktopRunning) return;
        var store = Regex.Match(path, @"\\WindowsApps\\(?<name>[^\\]+?)_[0-9.]+_[^\\]+__(?<publisher>[^\\]+)\\", RegexOptions.IgnoreCase);
        if (store.Success)
        {
            var appId = "Claude";
            try
            {
                var manifest = System.IO.Path.Combine(path[..(store.Index + store.Length)], "AppxManifest.xml");
                if (File.Exists(manifest) && Regex.Match(File.ReadAllText(manifest), "<Application\\s+Id=\"(?<id>[^\"]+)\"") is { Success: true } m) appId = m.Groups["id"].Value;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var shell = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            shell.ArgumentList.Add("shell:AppsFolder\\" + store.Groups["name"].Value + "_" + store.Groups["publisher"].Value + "!" + appId);
            Process.Start(shell);
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ---- process inspection without WMI -------------------------------------------------------------
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returned);
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    private const uint QueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    private static (string Path, string CommandLine) Inspect(int id)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, id);
        if (handle == IntPtr.Zero) return ("", "");
        try
        {
            var builder = new StringBuilder(32768); var length = builder.Capacity;
            var path = QueryFullProcessImageName(handle, 0, builder, ref length) ? builder.ToString() : "";
            var commandLine = "";
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
            if (needed > 0 && needed < 1 << 20)
            {
                var buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) == 0)
                    {
                        var text = Marshal.PtrToStructure<UnicodeString>(buffer);
                        if (text.Buffer != IntPtr.Zero) commandLine = Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return (path, commandLine);
        }
        finally { CloseHandle(handle); }
    }
}
