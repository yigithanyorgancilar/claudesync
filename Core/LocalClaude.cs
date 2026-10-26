using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeSync;

/// <summary>A Claude Desktop "Code" conversation on this computer.</summary>
/// <param name="CliId">Current transcript. Rewinding a conversation in Claude Desktop starts a new one.</param>
/// <param name="Prior">Earlier transcripts of the same conversation (priorCliSessionIds) that exist on disk: id → path.</param>
public sealed record LocalSession(string DesktopId, string CliId, string Title, string Cwd, string MetaPath,
    string? TranscriptPath, DateTimeOffset LastActivity, bool Archived, IReadOnlyList<string>? PriorCliIds = null,
    IReadOnlyDictionary<string, string>? Prior = null)
{
    public string ProjectName => Path.GetFileName(Cwd.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : Cwd;
    public IReadOnlyDictionary<string, string> PriorTranscripts => Prior ?? new Dictionary<string, string>();

    /// <summary>True if <paramref name="cliSessionId"/> is this conversation's current or an earlier transcript.</summary>
    public bool Owns(string cliSessionId) => CliId.Equals(cliSessionId, StringComparison.OrdinalIgnoreCase)
        || (PriorCliIds?.Contains(cliSessionId, StringComparer.OrdinalIgnoreCase) ?? false);
}

/// <summary>Finds Claude Desktop's session metadata and Claude Code's transcripts on disk.</summary>
public sealed class LocalClaude(AppConfig config)
{
    public string ProjectsRoot => config.ProjectsRoot
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static string Slug(string path) => Regex.Replace(path, "[^A-Za-z0-9]", "-");
    private static string P(JsonNode? node, string key) => node?[key]?.ToString() ?? "";

    public string DesktopRoot()
    {
        if (!string.IsNullOrEmpty(config.DesktopRoot)) return config.DesktopRoot;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<string> { Path.Combine(roaming, "Claude"), Path.Combine(local, "Claude-3p"), Path.Combine(local, "Claude") };
        // Since October 2026 Claude Desktop keeps its data in %LOCALAPPDATA%\Claude-Data\<install id> (e.g. claude_pzs8sxrjxfjjc)
        // and leaves only a ".user-data-left.*" marker in %APPDATA%\Claude.
        var dataRoot = Path.Combine(local, "Claude-Data");
        if (Directory.Exists(dataRoot)) candidates.AddRange(Directory.GetDirectories(dataRoot));
        var packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
            foreach (var package in Directory.GetDirectories(packages, "Claude_*"))
            {
                candidates.Add(Path.Combine(package, "LocalCache", "Roaming", "Claude"));
                var virtualized = Path.Combine(package, "LocalCache", "Local", "Claude-Data");
                if (Directory.Exists(virtualized)) candidates.AddRange(Directory.GetDirectories(virtualized));
            }
        // Several installs can leave data behind; the one written most recently is the one in use.
        var found = candidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(x => Directory.Exists(Path.Combine(x, "claude-code-sessions")))
            .OrderByDescending(x => LatestWrite(Path.Combine(x, "claude-code-sessions")))
            .FirstOrDefault();
        return found ?? throw new DirectoryNotFoundException(L.DesktopDataNotFound);
    }

    /// <summary>claude-code-sessions/&lt;account&gt;/&lt;workspace&gt; folder of the signed-in Claude account.</summary>
    public string Group()
    {
        var root = Path.Combine(DesktopRoot(), "claude-code-sessions");
        if (!string.IsNullOrEmpty(config.AccountId) && !string.IsNullOrEmpty(config.WorkspaceId))
        {
            var target = Path.Combine(root, config.AccountId, config.WorkspaceId);
            if (!Directory.Exists(target)) throw new DirectoryNotFoundException(L.AccountFolderNotFound);
            return target;
        }
        // Several Claude accounts may have signed in on this computer; each keeps its own folder. Guessing by
        // "most recently written" flips between them, so ask Claude Desktop which account is signed in.
        var accounts = Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        if (ActiveAccount() is { } active && accounts.FirstOrDefault(x => Path.GetFileName(x).Equals(active, StringComparison.OrdinalIgnoreCase)) is { } mine)
            accounts = [mine];
        var groups = accounts.SelectMany(Directory.GetDirectories).ToArray();
        if (groups.Length == 0) throw new DirectoryNotFoundException(L.NoCodeConversations);
        // Within one account, the organization whose conversations were used last.
        return groups.OrderByDescending(x => Directory.EnumerateFiles(x, "local_*.json").Any()).ThenByDescending(LatestWrite).First();
    }

    /// <summary>The account Claude Desktop is signed in with (config.json → lastKnownAccountUuid), if known.</summary>
    public string? ActiveAccount()
    {
        try
        {
            var file = Path.Combine(DesktopRoot(), "config.json");
            if (!File.Exists(file)) return null;
            return JsonNode.Parse(Store.ReadShared(file))?["lastKnownAccountUuid"]?.ToString() is { } id && Guid.TryParse(id, out _) ? id : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or DirectoryNotFoundException) { return null; }
    }

    private static DateTime LatestWrite(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "local_*.json", SearchOption.AllDirectories)
                .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(folder)).Max();
        }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// <summary>cliSessionId → transcript path. If a transcript exists in several project folders the newest wins.</summary>
    public Dictionary<string, string> TranscriptIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(ProjectsRoot)) return index;
        foreach (var folder in Directory.EnumerateDirectories(ProjectsRoot))
            foreach (var file in Directory.EnumerateFiles(folder, "*.jsonl"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (!Guid.TryParse(id, out _)) continue;
                if (!index.TryGetValue(id, out var existing) || File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(existing))
                    index[id] = file;
            }
        return index;
    }

    public IReadOnlyList<LocalSession> Sessions()
    {
        var group = Group();
        var transcripts = TranscriptIndex();
        var result = new List<LocalSession>();
        foreach (var metaPath in Directory.GetFiles(group, "local_*.json"))
        {
            try
            {
                var meta = JsonNode.Parse(Store.ReadShared(metaPath));
                var cli = P(meta, "cliSessionId");
                if (meta is null || !Guid.TryParse(cli, out _)) continue;
                var activity = long.TryParse(P(meta, "lastActivityAt"), out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : new DateTimeOffset(File.GetLastWriteTimeUtc(metaPath));
                var title = P(meta, "title") is { Length: > 0 } t ? t : L.Untitled;
                var priorIds = (meta["priorCliSessionIds"] as JsonArray ?? [])
                    .Select(x => x?.ToString() ?? "").Where(x => Guid.TryParse(x, out _) && !x.Equals(cli, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var prior = priorIds.Where(transcripts.ContainsKey).ToDictionary(x => x, x => transcripts[x], StringComparer.OrdinalIgnoreCase);
                result.Add(new(P(meta, "sessionId"), cli, title, P(meta, "cwd"), metaPath,
                    transcripts.GetValueOrDefault(cli), activity, P(meta, "isArchived") == "true", priorIds, prior));
            }
            catch (IOException) { }
            catch (JsonException) { }
        }
        return result.OrderByDescending(x => x.LastActivity).ToArray();
    }
}
