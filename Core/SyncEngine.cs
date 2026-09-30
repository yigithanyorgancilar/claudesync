using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeSync;

public enum Resolution { Auto, KeepLocal, TakeCloud }

public sealed record BackupItem(string SessionId, string Source, string Reference, DateTimeOffset Created, string Label, bool Current);

/// <summary>
/// Keeps each subscribed conversation equal to the cloud head.
///
/// A conversation is found by its Claude Desktop id (local_…), which never changes; its transcript does —
/// rewinding starts a new one and lists the old ones in priorCliSessionIds. All transcripts travel together.
///
/// Decisions compare the set of entry ids (uuid) across those transcripts, not raw bytes: Claude appends
/// bookkeeping lines whenever a conversation is merely opened, and a rewind removes the abandoned branch.
///   cloud unchanged since the last agreement → this computer's state is uploaded as it is,
///   this computer unchanged since then       → the cloud version is applied,
///   both changed → local ⊆ cloud: download, cloud ⊆ local: upload, otherwise a real conflict.
/// </summary>
public sealed class SyncEngine(AppConfig config, ICloudApi cloud, LocalClaude claude, string home, Func<ClaudeSnapshot>? processes = null)
{
    public const long MaxPackageBytes = 100L * 1024 * 1024;
    /// <summary>A conversation that is open in Claude is uploaded only after it has been quiet this long.</summary>
    public TimeSpan IdleTime { get; init; } = TimeSpan.FromSeconds(30);
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    private string StatePath => Path.Combine(home, "state.json");
    private string Folder(string name) => Path.Combine(home, name);
    private static string P(JsonNode? node, string key) => node?[key]?.ToString() ?? "";
    private static SessionReport Of(SyncStatus status, string message = "") => SessionReport.Of(status, message);

    public SyncState LoadState() => Store.Load<SyncState>(StatePath);
    public ClaudeSnapshot Processes() => (processes ?? ClaudeProcesses.Snapshot)();

    private sealed record Context(Dictionary<string, JsonNode> Cloud, SyncState State, ClaudeSnapshot Processes, IReadOnlyList<LocalSession> Local);

    private sealed record LocalCopy(LocalSession Session, Transcript Current, IReadOnlyDictionary<string, byte[]> Prior,
        HashSet<string> Ids, DateTime TranscriptWrite, DateTime LastWrite)
    {
        public string Sig { get; } = Transcript.Signature(Ids);
    }

    private sealed record Remote(string Head, byte[] Blob, Package Package, HashSet<string> Ids)
    {
        public string Sig { get; } = Transcript.Signature(Ids);
        private string? hash;
        /// <summary>Server-compatible transcript hash of this version.</summary>
        public string Hash => hash ??= Transcript.LegacyHash(Package.TranscriptBytes, Package.SourceCwd);
    }

    private async Task<Context> Load()
    {
        var library = await cloud.Library();
        var sessions = new Dictionary<string, JsonNode>();
        foreach (var node in library["sessions"]?.AsArray() ?? []) if (node is not null) sessions[P(node, "id")] = node;
        return new(sessions, LoadState(), Processes(), claude.Sessions());
    }

    /// <summary>The Desktop conversation behind a subscription: by Desktop id first, then by any of its transcript ids.</summary>
    private static LocalSession? FindLocal(Subscription sub, JsonNode? cloudSession, IReadOnlyList<LocalSession> local)
    {
        var desktopId = P(cloudSession, "desktop_session_id");
        return local.FirstOrDefault(x => desktopId.Length > 0 && x.DesktopId == desktopId)
            ?? local.FirstOrDefault(x => x.Owns(sub.CliSessionId));
    }

    public async Task<IReadOnlyList<SessionReport>> SyncAll(CancellationToken cancel = default)
    {
        var subscriptions = config.Subscriptions.ToList();
        if (subscriptions.Count == 0) return [];
        var context = await Load();
        var reports = new List<SessionReport>();
        foreach (var sub in subscriptions)
        {
            cancel.ThrowIfCancellationRequested();
            reports.Add(await Run(sub, context, Resolution.Auto));
        }
        return reports;
    }

    public async Task<SessionReport> SyncOne(string sessionId, Resolution resolution = Resolution.Auto)
    {
        var sub = config.Subscriptions.FirstOrDefault(x => x.SessionId == sessionId)
            ?? throw new InvalidOperationException(L.NotSyncedHere);
        return await Run(sub, await Load(), resolution);
    }

    private async Task<SessionReport> Run(Subscription sub, Context context, Resolution resolution)
    {
        SessionReport report;
        try { report = await Sync(sub, context, resolution); }
        catch (CloudException ex) when (ex.IsVersionConflict)
        { report = Of(SyncStatus.Retry, L.RetryConcurrent); }
        catch (Exception ex)
        {
            Store.Log($"Sync {sub.SessionId}: {ex}");
            report = Of(SyncStatus.Error, ex.Message);
        }
        report.SessionId = sub.SessionId;
        report.At = DateTimeOffset.Now;
        context.State.Reports[sub.SessionId] = report;
        Store.Save(StatePath, context.State);
        return report;
    }

    private async Task<SessionReport> Sync(Subscription sub, Context context, Resolution resolution)
    {
        var map = config.Projects.FirstOrDefault(x => x.Id == sub.ProjectId);
        if (map is null) return Of(SyncStatus.NeedsFolder, L.NoFolderSelected);
        if (!Directory.Exists(map.Path)) return Of(SyncStatus.NeedsFolder, L.FolderMissing(map.Path));
        if (!context.Cloud.TryGetValue(sub.SessionId, out var cloudSession))
            return Of(SyncStatus.MissingCloud, L.RemovedFromCloud);

        var head = P(cloudSession, "head_version_id");
        var baseline = context.State.Base.GetValueOrDefault(sub.SessionId) ?? new SyncBase();
        var session = FindLocal(sub, cloudSession, context.Local);
        var local = session?.TranscriptPath is null ? null : ReadLocal(session);

        if (head.Length == 0)
            return local is null
                ? Of(SyncStatus.MissingLocal, L.NeverUploaded)
                : await Upload(sub, map, local, "", baseline, context, userInitiated: resolution != Resolution.Auto);

        if (resolution == Resolution.Auto && local is not null && head == baseline.Head)
        {
            if (baseline.Sig is null && local.Prior.Count == 0 && baseline.Hash == Transcript.LegacyHash(local.Current.Bytes, local.Session.Cwd))
            {
                baseline.Sig = local.Sig; // state written by version 1
                context.State.Base[sub.SessionId] = baseline;
            }
            if (baseline.Sig == local.Sig) return Of(SyncStatus.UpToDate); // opening a conversation does not count
            // Nobody else published since we last agreed, so whatever changed here — new messages or a
            // rewind that dropped some — is the conversation's current state.
            if (baseline.Sig is not null) return await Upload(sub, map, local, head, baseline, context, userInitiated: false);
        }

        var remote = await Fetch(sub, cloudSession, head);
        if (local is null) return Install(sub, map, session, remote, context, remote.Ids.Count);

        var localOnly = local.Ids.Count(id => !remote.Ids.Contains(id));
        var remoteOnly = remote.Ids.Count(id => !local.Ids.Contains(id));
        if (localOnly == 0 && remoteOnly == 0)
        {
            context.State.Base[sub.SessionId] = new SyncBase { Head = head, Hash = remote.Hash, Sig = remote.Sig, SyncedAt = DateTimeOffset.Now };
            return Of(SyncStatus.UpToDate);
        }
        if (resolution == Resolution.KeepLocal || (resolution == Resolution.Auto && remoteOnly == 0))
            return await Upload(sub, map, local, head, baseline, context, userInitiated: resolution != Resolution.Auto,
                replaces: resolution == Resolution.KeepLocal ? head : null);
        // Nothing changed here since we last agreed: the cloud version is simply newer (it may also have
        // dropped entries, e.g. the other computer rewound or resolved a conflict the other way).
        var unchangedHere = baseline.Sig is not null && local.Sig == baseline.Sig;
        if (resolution == Resolution.TakeCloud || localOnly == 0 || unchangedHere)
            return Install(sub, map, session, remote, context, remoteOnly);

        SaveConflict(sub, map, local, remote, baseline);
        return new SessionReport
        {
            Status = SyncStatus.Conflict, LocalOnly = localOnly, RemoteOnly = remoteOnly, RemoteHead = head,
            Message = L.ConflictMessage
        };
    }

    private static LocalCopy ReadLocal(LocalSession session)
    {
        var transcriptWrite = File.GetLastWriteTimeUtc(session.TranscriptPath!);
        var lastWrite = new[] { transcriptWrite, File.Exists(session.MetaPath) ? File.GetLastWriteTimeUtc(session.MetaPath) : DateTime.MinValue }
            .Concat(session.PriorTranscripts.Values.Select(File.GetLastWriteTimeUtc)).Max();
        var current = Transcript.Parse(Store.ReadShared(session.TranscriptPath!), trimPartialTail: true);
        var ids = new HashSet<string>(current.Ids, StringComparer.Ordinal);
        var prior = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, path) in session.PriorTranscripts)
        {
            var parsed = Transcript.Parse(Store.ReadShared(path), trimPartialTail: true);
            prior[id] = parsed.Bytes;
            ids.UnionWith(parsed.Ids);
        }
        return new(session, current, prior, ids, transcriptWrite, lastWrite);
    }

    /// <summary>Why a cloud version cannot be written into Claude's files right now, or null if it can.</summary>
    public string? InstallBlocker(Subscription sub, LocalSession? session, ClaudeSnapshot snapshot)
    {
        if (snapshot.DesktopRunning) return L.BlockedDesktop;
        if (snapshot.IsOpen(sub.CliSessionId) || (session is not null && snapshot.IsOpen(session.CliId)))
            return L.BlockedTerminal;
        if (snapshot.AnonymousCli && session?.TranscriptPath is { } path && File.Exists(path)
            && UtcNow() - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(10))
            return L.BlockedMaybeTerminal;
        return null;
    }

    private SessionReport Install(Subscription sub, ProjectMap map, LocalSession? session, Remote remote, Context context, int remoteOnly)
    {
        if (InstallBlocker(sub, session, context.Processes) is { } reason)
            return new SessionReport { Status = SyncStatus.PendingDownload, Message = reason, RemoteOnly = remoteOnly, RemoteHead = remote.Head };
        WriteToClaude(sub, map, session, remote.Package);
        context.State.Base[sub.SessionId] = new SyncBase { Head = remote.Head, Hash = remote.Hash, Sig = remote.Sig, SyncedAt = DateTimeOffset.Now };
        return new SessionReport { Status = SyncStatus.Downloaded, RemoteOnly = remoteOnly };
    }

    private byte[] PackLocal(LocalCopy local, ProjectMap map, string parentHash, string? replaces = null)
    {
        var session = local.Session;
        var cwd = session.Cwd.Length > 0 ? session.Cwd : map.Path;
        return Package.Pack(Store.ReadShared(session.MetaPath), local.Current.Bytes, cwd,
            Path.GetFileName(Path.GetDirectoryName(session.TranscriptPath)) ?? "", parentHash, replaces, local.Prior);
    }

    private async Task<SessionReport> Upload(Subscription sub, ProjectMap map, LocalCopy local, string expectedHead, SyncBase baseline, Context context,
        bool userInitiated, string? replaces = null)
    {
        var quiet = UtcNow() - local.LastWrite;
        var open = context.Processes.IsOpen(local.Session.CliId) || context.Processes.IsOpen(sub.CliSessionId);
        if ((open && quiet < IdleTime) || (!userInitiated && quiet < TimeSpan.FromSeconds(3)))
            return Of(SyncStatus.WaitingIdle, L.Typing);

        var package = PackLocal(local, map, baseline.Hash ?? "", replaces);
        if (package.Length > MaxPackageBytes) return Of(SyncStatus.Error, L.PackageTooBig(package.Length / 1024 / 1024));
        if (File.GetLastWriteTimeUtc(local.Session.TranscriptPath!) != local.TranscriptWrite)
            return Of(SyncStatus.WaitingIdle, L.Typing);

        var cwd = local.Session.Cwd.Length > 0 ? local.Session.Cwd : map.Path;
        var hash = Transcript.LegacyHash(local.Current.Bytes, cwd);
        var id = await cloud.Upload(sub.SessionId, package, config.DeviceId, expectedHead, hash);
        Cache(sub.SessionId, id, package);
        context.State.Base[sub.SessionId] = new SyncBase { Head = id, Hash = hash, Sig = local.Sig, SyncedAt = DateTimeOffset.Now };
        return Of(SyncStatus.Uploaded);
    }

    // ---- cloud packages -----------------------------------------------------------------------------

    private string CachePath(string sessionId, string head) => Path.Combine(Folder("cache"), $"{sessionId}-{head}.zip");

    private void Cache(string sessionId, string head, byte[] package)
    {
        try
        {
            var folder = Folder("cache");
            if (Directory.Exists(folder))
                foreach (var old in Directory.GetFiles(folder, sessionId + "-*.zip")) File.Delete(old);
            Store.Atomic(CachePath(sessionId, head), package);
        }
        catch (IOException ex) { Store.Log("Cache write failed: " + ex.Message); }
    }

    private static bool BelongsTo(Package package, Subscription sub, JsonNode? cloudSession)
    {
        var desktopId = P(cloudSession, "desktop_session_id");
        return (desktopId.Length > 0 && package.SessionId == desktopId)
            || package.CliSessionId.Equals(sub.CliSessionId, StringComparison.OrdinalIgnoreCase)
            || package.Prior.ContainsKey(sub.CliSessionId);
    }

    private async Task<Remote> Fetch(Subscription sub, JsonNode? cloudSession, string head)
    {
        var cached = CachePath(sub.SessionId, head);
        byte[] blob;
        if (File.Exists(cached)) blob = File.ReadAllBytes(cached);
        else
        {
            var versions = await cloud.Versions(sub.SessionId);
            var version = versions.FirstOrDefault(x => P(x, "id") == head) ?? throw new InvalidDataException(L.VersionNotFound);
            blob = await cloud.Download(head);
            if (P(version, "package_hash") is { Length: > 0 } expected && Transcript.Hash(blob) != expected)
                throw new InvalidDataException(L.DownloadNotVerified);
            Cache(sub.SessionId, head, blob);
        }
        var package = Package.Unpack(blob);
        if (!BelongsTo(package, sub, cloudSession)) throw new InvalidDataException(L.PackageOtherConversation);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transcript in package.Transcripts) ids.UnionWith(Transcript.Parse(transcript).Ids);
        return new Remote(head, blob, package, ids);
    }

    private void SaveConflict(Subscription sub, ProjectMap map, LocalCopy local, Remote remote, SyncBase baseline)
    {
        var folder = Folder("conflicts");
        var cloudCopy = Path.Combine(folder, $"{sub.SessionId}-{remote.Head}-cloud.zip");
        if (!File.Exists(cloudCopy)) Store.Atomic(cloudCopy, remote.Blob);
        Store.Atomic(Path.Combine(folder, $"{sub.SessionId}-{remote.Head}-local.zip"), PackLocal(local, map, baseline.Hash ?? ""));
    }

    // ---- writing into Claude's files ------------------------------------------------------------------

    // Fields tied to this computer's Claude Desktop (Remote Control bridge); never copied from another device.
    private static readonly string[] DeviceKeys = ["bridgeSessionIds", "remoteControlUserEnabled", "remoteControlAutoEligible", "steeredByRemoteClient"];

    private void WriteToClaude(Subscription sub, ProjectMap map, LocalSession? session, Package package)
    {
        var metaPath = session?.MetaPath ?? Path.Combine(claude.Group(), package.SessionId + ".json");
        JsonObject? existingMeta = null;
        if (File.Exists(metaPath))
        {
            existingMeta = JsonNode.Parse(Store.ReadShared(metaPath)) as JsonObject;
            if (P(existingMeta, "sessionId") != package.SessionId)
                throw new InvalidOperationException(L.OtherConversationHere);
        }
        // Claude Code looks transcripts up in the folder named after the conversation's cwd.
        var folder = session?.TranscriptPath is { } currentPath && session.Cwd.Equals(map.Path, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(currentPath)!
            : Path.Combine(claude.ProjectsRoot, LocalClaude.Slug(map.Path));
        var targets = package.Prior.ToDictionary(x => Path.Combine(folder, x.Key + ".jsonl"), x => x.Value, StringComparer.OrdinalIgnoreCase);
        targets[Path.Combine(folder, package.CliSessionId + ".jsonl")] = package.TranscriptBytes;
        var existing = session is null ? [] : session.PriorTranscripts.Values.Prepend(session.TranscriptPath).OfType<string>().ToArray();
        var stale = existing.Where(x => !targets.ContainsKey(x)).ToArray();

        Backup(sub.CliSessionId, targets.Keys.Concat(stale).Prepend(metaPath).Where(File.Exists));

        var source = package.SourceCwd;
        var meta = (JsonObject)Relocate(package.Meta.DeepClone(), source, map.Path)!;
        meta["cwd"] = map.Path;
        meta["isArchived"] = false;
        meta.Remove("transcriptUnavailable");
        foreach (var key in DeviceKeys)
        {
            meta.Remove(key);
            if (existingMeta?[key] is { } mine) meta[key] = mine.DeepClone();
        }

        foreach (var (path, bytes) in targets) Store.Atomic(path, Transcript.Relocate(bytes, source, map.Path));
        Store.Atomic(metaPath, Encoding.UTF8.GetBytes(meta.ToJsonString(Store.Json)));
        foreach (var path in stale) File.Delete(path);
    }

    /// <summary>Rewrites every string in the metadata that is the source folder or inside it
    /// (cwd, originCwd, rewindEdges[].cwd, "folder\0branch" entries, …).</summary>
    private static JsonNode? Relocate(JsonNode? node, string source, string target)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(x => x.Key).ToArray())
                    if (Relocate(obj[key], source, target) is var child && !ReferenceEquals(child, obj[key])) obj[key] = child;
                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    if (Relocate(array[i], source, target) is var item && !ReferenceEquals(item, array[i])) array[i] = item;
                return array;
            case JsonValue value when value.TryGetValue<string>(out var text):
                var cut = text.IndexOf('\0') is var c && c >= 0 ? c : text.Length;
                var moved = Transcript.RelocatePath(text[..cut], source, target) + text[cut..];
                return moved == text ? value : JsonValue.Create(moved);
            default:
                return node;
        }
    }

    private void Backup(string cliId, IEnumerable<string> files)
    {
        var existing = files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (existing.Length == 0) return;
        var root = Folder("backups");
        var target = Path.Combine(root, cliId + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
        Directory.CreateDirectory(target);
        foreach (var path in existing) Store.Atomic(Path.Combine(target, Path.GetFileName(path)), Store.ReadShared(path));
        // Automatic backups of one conversation: keep the newest ten.
        foreach (var old in Directory.GetDirectories(root, cliId + "-*").OrderDescending(StringComparer.Ordinal).Skip(10))
        {
            try { Directory.Delete(old, true); } catch (IOException) { }
        }
    }

    // ---- backups & restore ----------------------------------------------------------------------------

    public async Task<IReadOnlyList<BackupItem>> Backups(string sessionId, string cliId)
    {
        var result = new List<BackupItem>();
        var library = await cloud.Library();
        var session = library["sessions"]?.AsArray().FirstOrDefault(x => P(x, "id") == sessionId);
        var head = P(session, "head_version_id");
        var devices = (library["devices"]?.AsArray() ?? []).Where(x => x is not null).ToDictionary(x => P(x, "id"), x => P(x, "name"));
        foreach (var version in await cloud.Versions(sessionId))
        {
            var stamp = DateTimeOffset.TryParse(P(version, "created_at"), out var date) ? date : DateTimeOffset.MinValue;
            var device = devices.GetValueOrDefault(P(version, "author_device_id"));
            result.Add(new(sessionId, "cloud", P(version, "id"), stamp, device is { Length: > 0 } ? L.CloudVersion + " · " + device : L.CloudVersion, P(version, "id") == head));
        }
        var conflicts = Folder("conflicts");
        if (Directory.Exists(conflicts))
            foreach (var file in Directory.GetFiles(conflicts, sessionId + "-*.zip"))
                result.Add(new(sessionId, "zip", file, File.GetLastWriteTime(file),
                    file.EndsWith("-local.zip", StringComparison.Ordinal) ? L.ConflictLocalCopy : L.ConflictCloudCopy, false));
        var backups = Folder("backups");
        if (Directory.Exists(backups))
            foreach (var folder in Directory.GetDirectories(backups, cliId + "-*"))
                result.Add(new(sessionId, "folder", folder, Directory.GetCreationTime(folder), L.AutoBackup, false));
        return result.OrderByDescending(x => x.Created).ToArray();
    }

    /// <summary>Writes a backup into Claude's files and publishes it as the newest cloud version.</summary>
    public async Task<SessionReport> Restore(BackupItem item)
    {
        var sub = config.Subscriptions.FirstOrDefault(x => x.SessionId == item.SessionId)
            ?? throw new InvalidOperationException(L.RestoreNeedsSync);
        var map = config.Projects.FirstOrDefault(x => x.Id == sub.ProjectId)
            ?? throw new InvalidOperationException(L.NoFolderSelected);
        var context = await Load();
        var cloudSession = context.Cloud.GetValueOrDefault(sub.SessionId);
        var session = FindLocal(sub, cloudSession, context.Local);
        if (InstallBlocker(sub, session, context.Processes) is { } reason)
            throw new InvalidOperationException(L.RestoreNeedsClosed + reason);

        byte[] blob = item.Source switch
        {
            "cloud" => await cloud.Download(item.Reference),
            "zip" => File.ReadAllBytes(item.Reference),
            "folder" => PackFolder(item.Reference),
            _ => throw new InvalidOperationException(L.UnknownBackup)
        };
        var package = Package.Unpack(blob);
        if (!BelongsTo(package, sub, cloudSession)) throw new InvalidDataException(L.BackupOtherConversation);
        WriteToClaude(sub, map, session, package);
        return await SyncOne(sub.SessionId, Resolution.KeepLocal);
    }

    private static byte[] PackFolder(string folder)
    {
        var metaBytes = File.ReadAllBytes(Directory.GetFiles(folder, "local_*.json").Single());
        var meta = JsonNode.Parse(metaBytes);
        var transcripts = Directory.GetFiles(folder, "*.jsonl").ToDictionary(x => Path.GetFileNameWithoutExtension(x), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        var current = P(meta, "cliSessionId");
        if (!transcripts.Remove(current, out var currentBytes))
        {
            if (transcripts.Count != 1) throw new InvalidDataException(L.BackupNoCurrent);
            currentBytes = transcripts.Values.Single(); // backups made by version 2.0 hold one transcript
            transcripts.Clear();
        }
        return Package.Pack(metaBytes, currentBytes, P(meta, "cwd"), "", "", null, transcripts);
    }
}
