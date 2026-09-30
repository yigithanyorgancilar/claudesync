using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeSync;

// config.json / state.json keep the field names of the first version so existing installs keep working.
public sealed class AppConfig
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("supabase_url")] public string SupabaseUrl { get; set; } = "";
    [JsonPropertyName("supabase_publishable_key")] public string PublishableKey { get; set; } = "";
    [JsonPropertyName("worker_url")] public string WorkerUrl { get; set; } = "";
    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("projects")] public List<ProjectMap> Projects { get; set; } = [];
    [JsonPropertyName("subscriptions")] public List<Subscription> Subscriptions { get; set; } = [];
    /// <summary>"en", "tr" or null for the Windows display language.</summary>
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonIgnore] public bool ServerConfigured => IsHttps(SupabaseUrl) && IsHttps(WorkerUrl) && PublishableKey.Length > 0;
    public static bool IsHttps(string value) => Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    // A single line that carries the three server values to another computer. The publishable key is meant
    // to be public; the code contains no secret.
    private const string CodePrefix = "claudesync1:";
    public string ConnectionCode() => CodePrefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
        new Dictionary<string, string> { ["s"] = SupabaseUrl, ["k"] = PublishableKey, ["w"] = WorkerUrl })).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static (string Supabase, string Key, string Worker)? ParseConnectionCode(string code)
    {
        code = code.Trim();
        if (!code.StartsWith(CodePrefix, StringComparison.Ordinal)) return null;
        try
        {
            var body = code[CodePrefix.Length..].Replace('-', '+').Replace('_', '/');
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(Convert.FromBase64String(body.PadRight((body.Length + 3) / 4 * 4, '=')));
            if (values is null || !values.TryGetValue("s", out var s) || !values.TryGetValue("k", out var k) || !values.TryGetValue("w", out var w)) return null;
            return IsHttps(s) && IsHttps(w) && k.Length > 0 ? (s, k, w) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }
    [JsonPropertyName("desktop_root")] public string? DesktopRoot { get; set; }
    [JsonPropertyName("account_id")] public string? AccountId { get; set; }
    [JsonPropertyName("workspace_id")] public string? WorkspaceId { get; set; }
    [JsonPropertyName("projects_root")] public string? ProjectsRoot { get; set; }
}
public sealed class ProjectMap
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}
public sealed class Subscription
{
    [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("cli_session_id")] public string CliSessionId { get; set; } = "";
}
public sealed class SyncState
{
    [JsonPropertyName("version")] public int Version { get; set; } = 2;
    [JsonPropertyName("base")] public Dictionary<string, SyncBase> Base { get; set; } = [];
    [JsonPropertyName("reports")] public Dictionary<string, SessionReport> Reports { get; set; } = [];
}
/// <summary>What this device last agreed on with the cloud for one conversation.</summary>
public sealed class SyncBase
{
    /// <summary>Cloud version id both sides had in common.</summary>
    [JsonPropertyName("head")] public string? Head { get; set; }
    /// <summary>Legacy full-content hash (server-side transcript_hash) of that version.</summary>
    [JsonPropertyName("hash")] public string? Hash { get; set; }
    /// <summary>Hash of the set of message ids in that version. Metadata-only lines do not change it.</summary>
    [JsonPropertyName("sig")] public string? Sig { get; set; }
    [JsonPropertyName("synced_at")] public DateTimeOffset? SyncedAt { get; set; }
}

public enum SyncStatus { UpToDate, Uploaded, Downloaded, PendingDownload, WaitingIdle, Conflict, NeedsFolder, MissingLocal, MissingCloud, Retry, Error }

public sealed class SessionReport
{
    [JsonPropertyName("status"), JsonConverter(typeof(JsonStringEnumConverter<SyncStatus>))] public SyncStatus Status { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("at")] public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    [JsonPropertyName("local_only")] public int LocalOnly { get; set; }
    [JsonPropertyName("remote_only")] public int RemoteOnly { get; set; }
    [JsonPropertyName("remote_head")] public string? RemoteHead { get; set; }
    [JsonIgnore] public string SessionId { get; set; } = "";

    public static SessionReport Of(SyncStatus status, string message = "") => new() { Status = status, Message = message };
}

public sealed class AuthTokens
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("expires_at")] public double ExpiresAt { get; set; }
}

public static class Store
{
    public static readonly string Home = Environment.GetEnvironmentVariable("CLAUDESYNC_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeSyncCloud");
    public static string ConfigPath => Path.Combine(Home, "config.json");
    public static string AuthPath => Path.Combine(Home, "auth.dpapi");
    public static string LogPath => Path.Combine(Home, "claudesync-desktop.log");
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    // Must stay byte-identical to the first version: it feeds the transcript hash other devices verify.
    public static readonly JsonSerializerOptions CompactJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly object LogGate = new();

    public static T Load<T>(string path) where T : new()
    {
        if (!File.Exists(path)) return new T();
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? new T(); }
        catch (JsonException ex)
        {
            // Keep the damaged file for inspection instead of silently overwriting it later.
            var aside = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            try { File.Copy(path, aside, true); } catch (IOException) { }
            Log($"Could not read {path}, copied to {aside}: {ex.Message}");
            return new T();
        }
    }
    public static void Save<T>(string path, T value) => Atomic(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));

    public static void Log(string message)
    {
        lock (LogGate)
        {
            try
            {
                Directory.CreateDirectory(Home);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2 * 1024 * 1024)
                    File.Move(LogPath, LogPath + ".old", true);
                File.AppendAllText(LogPath, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch (IOException) { }
        }
    }

    public static void Atomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>Reads a file that Claude may hold open for appending.</summary>
    public static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, int.MaxValue) : 0);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static AuthTokens LoadTokens() => File.Exists(AuthPath)
        ? JsonSerializer.Deserialize<AuthTokens>(Unprotect(File.ReadAllBytes(AuthPath)), Json) ?? new() : new();
    public static void SaveTokens(AuthTokens tokens) => Atomic(AuthPath, Protect(JsonSerializer.SerializeToUtf8Bytes(tokens, Json)));
    public static void DeleteTokens() { if (File.Exists(AuthPath)) File.Delete(AuthPath); }

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr handle);
    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, input.Data, data.Length);
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new InvalidOperationException(L.SavedSignInUnreadable);
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
    private static byte[] Protect(byte[] data) => Transform(data, true);
    private static byte[] Unprotect(byte[] data) => Transform(data, false);
}
