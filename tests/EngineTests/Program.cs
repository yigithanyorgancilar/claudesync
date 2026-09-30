using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeSync.Tests;

internal static class Program
{
    private static int failures;

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--inspect-local")) { InspectLocal(); return 0; }
        var root = Path.Combine(Path.GetTempPath(), "claudesync-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("CLAUDESYNC_HOME", Path.Combine(root, "log")); // keep test errors out of the real log
        try
        {
            await Test("Opening a conversation on the other computer does not overwrite newer messages", () => ReportedBug(Path.Combine(root, "1")));
            await Test("Recovers when an old client already pushed the stale copy", () => RecoverFromStaleUpload(Path.Combine(root, "2")));
            await Test("Real conflict is detected and resolving it propagates", () => RealConflict(Path.Combine(root, "3")));
            await Test("State files from version 1 are migrated without re-uploading", () => MigrateV1State(Path.Combine(root, "4")));
            await Test("A half-written last line is not uploaded", () => PartialLine(Path.Combine(root, "5")));
            await Test("A rewound conversation (new transcript id) keeps syncing", () => Rewound(Path.Combine(root, "7")));
            await Test("Removing a conversation in the cloud leaves local files alone", () => RemovedElsewhere(Path.Combine(root, "6")));
            await Test("Uses the Claude account Desktop is signed in with", () => AccountDetection(Path.Combine(root, "8")));
            await Test("Legacy hash matches version 1 algorithm", LegacyHashCompat);
            await Test("Process classification", ProcessClassification);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        Console.WriteLine(failures == 0 ? "\nALL PASSED" : $"\n{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static async Task Test(string name, Func<Task> body)
    {
        try { await body(); Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL  " + name + "\n      " + ex.Message.Replace("\n", "\n      ")); }
    }
    private static Task Test(string name, Action body) => Test(name, () => { body(); return Task.CompletedTask; });
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Is(SessionReport report, SyncStatus expected, string step)
        => Check(report.Status == expected, $"{step}: expected {expected}, got {report.Status} ({report.Message})");

    // ---- scenarios ------------------------------------------------------------------------------------

    /// <summary>
    /// The bug that was reported: A uploads, B downloads, B merely opens the conversation (Claude appends
    /// bookkeeping lines), A continues the conversation. Version 1 pushed B's stale copy to the cloud here.
    /// </summary>
    private static async Task ReportedBug(string root)
    {
        var cloud = new FakeCloud();
        var a = new Device(root, "A", cloud, @"proj");
        var b = new Device(root, "B", cloud, @"work\proj");
        a.CreateConversation("m1", "m2");
        a.AppendRaw(a.Line("m2b", "m2", a.ProjectPath + @"\supabase\functions"));
        cloud.AddSession(Device.SessionId, Device.CliId);
        a.Subscribe(); b.Subscribe();

        Is(await a.Sync(), SyncStatus.Uploaded, "A first upload");
        Is(await b.Sync(), SyncStatus.Downloaded, "B first download");
        var bText = b.TranscriptText();
        Check(bText.Contains("\"m2\"") && !bText.Contains(Escape(a.ProjectPath) + "\""), "B transcript was not moved to B's folder");
        Check(bText.Contains(Escape(b.ProjectPath + @"\supabase\functions")), "sub-folder cwd was not moved to B's folder");
        Check(File.Exists(b.MetaPath) && JsonNode.Parse(File.ReadAllText(b.MetaPath))!["cwd"]!.ToString() == b.ProjectPath, "B metadata cwd is wrong");

        b.OpenInClaude(); // Claude appends custom-title / mode / bridge-session lines
        Is(await b.Sync(), SyncStatus.UpToDate, "B after only opening the conversation");
        Check(cloud.VersionCount == 1, "B uploaded although it had no new messages");

        a.OpenInClaude();
        a.Append("m3", "m4");
        Is(await a.Sync(), SyncStatus.Uploaded, "A upload of new messages");

        b.DesktopRunning = true;
        Is(await b.Sync(), SyncStatus.PendingDownload, "B while Claude Desktop is open");
        Check(!b.TranscriptText().Contains("\"m4\""), "B's files were touched while Claude was open");
        b.DesktopRunning = false;
        Is(await b.Sync(), SyncStatus.Downloaded, "B after Claude Desktop closed");
        Check(b.TranscriptText().Contains("\"m4\""), "B did not receive A's new messages");
        Is(await a.Sync(), SyncStatus.UpToDate, "A afterwards");
        Is(await b.Sync(), SyncStatus.UpToDate, "B afterwards");
        Check(Directory.GetDirectories(Path.Combine(b.Home, "backups")).Length >= 1, "B made no backup before overwriting");

        // and the other direction, with the conversation open (idle) in Claude on B
        b.OpenInClaude();
        b.Append("m5");
        b.SessionOpen = true; b.DesktopRunning = true;
        b.Age(TimeSpan.FromSeconds(5));
        Is(await b.Sync(), SyncStatus.WaitingIdle, "B while still typing");
        b.Age(TimeSpan.FromMinutes(1));
        Is(await b.Sync(), SyncStatus.Uploaded, "B after being idle");
        Is(await a.Sync(), SyncStatus.Downloaded, "A receives B's message");
        Check(a.TranscriptText().Contains("\"m5\""), "A did not receive m5");
    }

    /// <summary>The cloud head is a stale copy with extra bookkeeping lines (what version 1 uploaded).</summary>
    private static async Task RecoverFromStaleUpload(string root)
    {
        var cloud = new FakeCloud();
        var a = new Device(root, "A", cloud, "proj");
        var b = new Device(root, "B", cloud, "proj");
        a.CreateConversation("m1", "m2");
        cloud.AddSession(Device.SessionId, Device.CliId);
        a.Subscribe(); b.Subscribe();
        Is(await a.Sync(), SyncStatus.Uploaded, "A first upload");
        Is(await b.Sync(), SyncStatus.Downloaded, "B first download");

        b.OpenInClaude();
        await cloud.Upload(Device.SessionId, b.PackLikeVersion1(), "B", cloud.Head(Device.SessionId), "legacy"); // the old bug
        a.Append("m3", "m4");

        Is(await a.Sync(), SyncStatus.Uploaded, "A pushes its newer conversation over the stale copy");
        Is(await b.Sync(), SyncStatus.Downloaded, "B gets the newer conversation");
        Check(b.TranscriptText().Contains("\"m4\""), "B is still stale");
    }

    private static async Task RealConflict(string root)
    {
        var cloud = new FakeCloud();
        var a = new Device(root, "A", cloud, "proj");
        var b = new Device(root, "B", cloud, "proj");
        a.CreateConversation("m1");
        cloud.AddSession(Device.SessionId, Device.CliId);
        a.Subscribe(); b.Subscribe();
        Is(await a.Sync(), SyncStatus.Uploaded, "A first upload");
        Is(await b.Sync(), SyncStatus.Downloaded, "B first download");

        a.Append("a2");
        b.Append("b2");
        Is(await a.Sync(), SyncStatus.Uploaded, "A upload");
        var conflict = await b.Sync();
        Is(conflict, SyncStatus.Conflict, "B");
        Check(conflict.LocalOnly == 1 && conflict.RemoteOnly == 1, $"conflict counts {conflict.LocalOnly}/{conflict.RemoteOnly}");
        Check(Directory.GetFiles(Path.Combine(b.Home, "conflicts")).Length == 2, "conflict copies were not saved");
        Check(b.TranscriptText().Contains("\"b2\""), "B's local conversation was changed during a conflict");
        Is(await b.Sync(), SyncStatus.Conflict, "B again (stays in conflict, nothing overwritten)");

        Is(await b.Engine().SyncOne(Device.SessionId, Resolution.KeepLocal), SyncStatus.Uploaded, "B keeps its version");
        Is(await a.Sync(), SyncStatus.Downloaded, "A follows B's decision");
        Check(a.TranscriptText().Contains("\"b2\"") && !a.TranscriptText().Contains("\"a2\""), "A did not take B's version");
        var backups = await a.Engine().Backups(Device.SessionId, Device.CliId);
        Check(backups.Any(x => x.Source == "folder"), "A's replaced version is not listed in backups");

        // TakeCloud path
        a.Append("a3"); b.Append("b3");
        Is(await a.Sync(), SyncStatus.Uploaded, "A upload 2");
        Is(await b.Sync(), SyncStatus.Conflict, "B conflict 2");
        Is(await b.Engine().SyncOne(Device.SessionId, Resolution.TakeCloud), SyncStatus.Downloaded, "B takes the cloud version");
        Check(b.TranscriptText().Contains("\"a3\"") && !b.TranscriptText().Contains("\"b3\""), "B did not take the cloud version");
        Is(await a.Sync(), SyncStatus.UpToDate, "A afterwards");
    }

    private static async Task MigrateV1State(string root)
    {
        var cloud = new FakeCloud();
        var a = new Device(root, "A", cloud, "proj");
        a.CreateConversation("m1", "m2");
        cloud.AddSession(Device.SessionId, Device.CliId);
        a.Subscribe();
        Is(await a.Sync(), SyncStatus.Uploaded, "upload");
        var statePath = Path.Combine(a.Home, "state.json");
        var state = Store.Load<SyncState>(statePath);
        state.Base[Device.SessionId].Sig = null; // what version 1 wrote
        state.Reports.Clear();
        Store.Save(statePath, state);
        var downloads = cloud.Downloads;
        Is(await a.Sync(), SyncStatus.UpToDate, "after migration");
        Check(cloud.VersionCount == 1 && cloud.Downloads == downloads, "migration caused network work");
        Check(Store.Load<SyncState>(statePath).Base[Device.SessionId].Sig is not null, "sig was not recorded");
    }

    private static async Task PartialLine(string root)
    {
        var cloud = new FakeCloud();
        var a = new Device(root, "A", cloud, "proj");
        a.CreateConversation("m1");
        a.AppendRaw("{\"type\":\"user\",\"uuid\":\"half", newline: false);
        cloud.AddSession(Device.SessionId, Device.CliId);
        a.Subscribe();
        Is(await a.Sync(), SyncStatus.Uploaded, "upload");
        var package = Package.Unpack(await cloud.Download(cloud.Head(Device.SessionId)));
        Check(!Encoding.UTF8.GetString(package.TranscriptBytes).Contains("half"), "partial line was uploaded");
    }

    private static async Task Rewound(string root)
    {
        const string newCli = "44444444-4444-4444-4444-444444444444";
        var cloud = new FakeCloud();
        var a = new Device(root, "A", cloud, "proj");
        var b = new Device(root, "B", cloud, @"work\proj");
        a.CreateConversation("m1", "m2", "m3", "m4");
        cloud.AddSession(Device.SessionId, Device.CliId);
        a.Subscribe(); b.Subscribe();
        Is(await a.Sync(), SyncStatus.Uploaded, "A first upload");
        Is(await b.Sync(), SyncStatus.Downloaded, "B first download");

        a.Rewind(newCli, "m2", "r3", "r4"); // m3, m4 are dropped from the old transcript
        Is(await a.Sync(), SyncStatus.Uploaded, "A after rewind (the reported bug: it could not find the conversation)");
        Is(await b.Sync(), SyncStatus.Downloaded, "B receives the rewound conversation");
        var newText = File.ReadAllText(b.TranscriptFor(newCli));
        Check(newText.Contains("\"r4\"") && newText.Contains("\"m1\""), "B's new transcript is incomplete");
        Check(!File.ReadAllText(b.TranscriptFor(Device.CliId)).Contains("\"m3\""), "B still has the abandoned branch");
        var meta = b.Meta();
        Check(meta["cliSessionId"]!.ToString() == newCli, "B's metadata does not point at the new transcript");
        Check(meta["rewindEdges"]![0]!["cwd"]!.ToString() == b.ProjectPath, "rewindEdges cwd was not moved to B's folder");
        Is(await a.Sync(), SyncStatus.UpToDate, "A afterwards");
        Is(await b.Sync(), SyncStatus.UpToDate, "B afterwards");

        b.Follow(newCli);
        b.Append("b5");
        Is(await b.Sync(), SyncStatus.Uploaded, "B continues the rewound conversation");
        Is(await a.Sync(), SyncStatus.Downloaded, "A receives it");
        Check(File.ReadAllText(a.TranscriptFor(newCli)).Contains("\"b5\""), "A did not receive b5");
    }

    private static async Task RemovedElsewhere(string root)
    {
        var cloud = new FakeCloud();
        var a = new Device(root, "A", cloud, "proj");
        var b = new Device(root, "B", cloud, "proj");
        a.CreateConversation("m1", "m2");
        cloud.AddSession(Device.SessionId, Device.CliId);
        a.Subscribe(); b.Subscribe();
        Is(await a.Sync(), SyncStatus.Uploaded, "A upload");
        Is(await b.Sync(), SyncStatus.Downloaded, "B download");
        var before = b.TranscriptText();
        cloud.Remove(Device.SessionId);
        Is(await b.Sync(), SyncStatus.MissingCloud, "B after removal");
        Check(b.TranscriptText() == before && File.Exists(b.MetaPath), "B's conversation was touched");
    }

    private static void AccountDetection(string root)
    {
        const string active = "aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa", other = "bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb";
        var sessions = Path.Combine(root, "claude-code-sessions");
        var mine = Path.Combine(sessions, active, "cccccccc-3333-4333-8333-cccccccccccc");
        var empty = Path.Combine(sessions, active, "dddddddd-4444-4444-8444-dddddddddddd");
        var theirs = Path.Combine(sessions, other, "dddddddd-4444-4444-8444-dddddddddddd");
        foreach (var dir in new[] { mine, empty, theirs }) Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(mine, "local_a.json"), "{}");
        File.WriteAllText(Path.Combine(empty, "scheduled-tasks.json"), "{}");
        File.WriteAllText(Path.Combine(theirs, "local_b.json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(theirs, "local_b.json"), DateTime.UtcNow.AddMinutes(1)); // written last
        var claude = new LocalClaude(new AppConfig { DesktopRoot = root });
        Check(claude.Group() == theirs, "without config.json the newest folder is used");
        File.WriteAllText(Path.Combine(root, "config.json"), "{\"lastKnownAccountUuid\":\"" + active + "\"}");
        Check(claude.Group() == mine, $"picked {claude.Group()}");
    }

    private static void LegacyHashCompat()
    {
        const string cwd = @"C:\Users\x\proj";
        var text = "{\"type\":\"user\",\"uuid\":\"u1\",\"cwd\":\"C:\\\\Users\\\\x\\\\proj\",\"note\":\"ş\"}\r\n{\"type\":\"mode\"}\n\n{\"cwd\":\"C:\\\\Users\\\\x\\\\proj\\\\sub\",\"uuid\":\"u2\"}";
        var bytes = Encoding.UTF8.GetBytes(text);
        Check(Transcript.LegacyHash(bytes, cwd) == Version1Hash(bytes, cwd), "legacy hash differs from version 1");
    }

    // Verbatim port of version 1's hashing so the compatibility check is meaningful.
    private static string Version1Hash(byte[] data, string source)
    {
        var text = Encoding.UTF8.GetString(data);
        var output = new StringBuilder(text.Length);
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, ".*?(?:\\r\\n|\\n|$)", System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            var line = match.Value;
            if (line.Length == 0) continue;
            var ending = line.EndsWith("\r\n") ? "\r\n" : line.EndsWith('\n') ? "\n" : "";
            var core = ending.Length == 0 ? line : line[..^ending.Length];
            if (core.Length == 0) { output.Append(ending); continue; }
            var node = JsonNode.Parse(core)!;
            var changed = false;
            foreach (var key in new[] { "cwd", "originCwd" })
                if (node[key]?.ToString() == source) { node[key] = Transcript.Placeholder; changed = true; }
            output.Append(changed ? node.ToJsonString(Store.CompactJson) : core).Append(ending);
        }
        return Transcript.Hash(Encoding.UTF8.GetBytes(output.ToString()));
    }

    private static void ProcessClassification()
    {
        Check(ClaudeProcesses.Classify(@"C:\Program Files\WindowsApps\Claude_2.9939.2.0_x64__pzs8sxrjxfjjc\app\claude.exe", "") == ClaudeProcessKind.Desktop, "store app");
        Check(ClaudeProcesses.Classify(@"C:\Users\y\AppData\Local\AnthropicClaude\app-1.0.0\claude.exe", "") == ClaudeProcessKind.Desktop, "squirrel app");
        Check(ClaudeProcesses.Classify(@"C:\Users\y\AppData\Roaming\Claude\claude-code\2.1.281\claude.exe", "--resume=x") == ClaudeProcessKind.DesktopSession, "desktop session");
        Check(ClaudeProcesses.Classify(@"C:\Users\y\.local\bin\claude.exe", "\"claude.exe\" --chrome-native-host") == ClaudeProcessKind.Helper, "chrome host");
        Check(ClaudeProcesses.Classify(@"C:\Users\y\.local\bin\claude.exe", "claude") == ClaudeProcessKind.Cli, "terminal");
        Check(ClaudeProcesses.SessionArgument("claude.exe --verbose --resume=eeeeeeee-5555-4555-8555-eeeeeeeeeeee --x") == "eeeeeeee-5555-4555-8555-eeeeeeeeeeee", "--resume=");
        Check(ClaudeProcesses.SessionArgument("claude -r eeeeeeee-5555-4555-8555-eeeeeeeeeeee") == "eeeeeeee-5555-4555-8555-eeeeeeeeeeee", "-r");
        Check(ClaudeProcesses.SessionArgument("claude --continue") is null, "--continue");
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\");

    /// <summary>Read-only look at this computer's real Claude data (no network, no writes).</summary>
    private static void InspectLocal()
    {
        var config = Store.Load<AppConfig>(Store.ConfigPath);
        var claude = new LocalClaude(config);
        Console.WriteLine("Group: " + claude.Group());
        var sessions = claude.Sessions();
        Console.WriteLine($"Sessions: {sessions.Count}");
        var state = Store.Load<SyncState>(Path.Combine(Store.Home, "state.json"));
        foreach (var sub in config.Subscriptions)
        {
            var found = sessions.FirstOrDefault(x => x.Owns(sub.CliSessionId));
            if (found?.TranscriptPath is not null)
            {
                var ids = new HashSet<string>(Transcript.Parse(Store.ReadShared(found.TranscriptPath), true).Ids);
                foreach (var path in found.PriorTranscripts.Values) ids.UnionWith(Transcript.Parse(Store.ReadShared(path), true).Ids);
                var baseline = state.Base.GetValueOrDefault(sub.SessionId);
                Console.WriteLine($"  subscription {sub.CliSessionId[..8]} → \"{found.Title}\" {found.DesktopId}, current {found.CliId[..8]}, prior [{string.Join(",", found.PriorTranscripts.Keys.Select(k => k[..8]))}], "
                    + $"{ids.Count} ids, changed since last sync: {baseline?.Sig != Transcript.Signature(ids)}, base head {baseline?.Head?[..8]}");
            }
            var s = sessions.FirstOrDefault(x => x.CliId == sub.CliSessionId);
            if (s?.TranscriptPath is null) { Console.WriteLine($"  {sub.SessionId}: not found locally"); continue; }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var bytes = Store.ReadShared(s.TranscriptPath);
            var t = Transcript.Parse(bytes, trimPartialTail: true);
            var parse = watch.ElapsedMilliseconds;
            var legacy = Transcript.LegacyHash(t.Bytes, s.Cwd);
            Console.WriteLine($"  \"{s.Title}\" {bytes.Length / 1024} KB, {t.Ids.Count} entries, last {t.LastEntryAt:u}, parse {parse} ms, legacy hash {watch.ElapsedMilliseconds - parse} ms ({legacy[..12]}), cwd {s.Cwd}");
        }
        foreach (var p in ClaudeProcesses.Snapshot().Processes)
            Console.WriteLine($"  process {p.Id}: {p.Kind}{(p.SessionId is null ? "" : " session " + p.SessionId)}");
    }
}

/// <summary>One simulated computer: its own Claude folders, project folder and ClaudeSync home.</summary>
internal sealed class Device
{
    public const string SessionId = "11111111-1111-1111-1111-111111111111";
    public const string CliId = "22222222-2222-2222-2222-222222222222";
    public const string DesktopId = "local_33333333-3333-3333-3333-333333333333";
    private const string ProjectId = "p1";

    private readonly FakeCloud cloud;
    private readonly AppConfig config;
    private DateTime now = DateTime.UtcNow.AddHours(1);
    public string Name { get; }
    public string Home { get; }
    public string ProjectPath { get; }
    private string Group { get; }
    private string ProjectsRoot { get; }
    public bool DesktopRunning { get; set; }
    public bool SessionOpen { get; set; }
    public string MetaPath => Path.Combine(Group, DesktopId + ".json");
    public string TranscriptPath => TranscriptFor(current);
    public string TranscriptFor(string cli) => Path.Combine(ProjectsRoot, LocalClaude.Slug(ProjectPath), cli + ".jsonl");
    private string current = CliId;
    private string lastId = "";

    public Device(string root, string name, FakeCloud cloud, string project)
    {
        this.cloud = cloud;
        Name = name;
        var device = Path.Combine(root, name);
        Home = Path.Combine(device, "home");
        ProjectPath = Path.Combine(device, project);
        Group = Path.Combine(device, "desktop", "claude-code-sessions", "acc", "ws");
        ProjectsRoot = Path.Combine(device, "projects");
        Directory.CreateDirectory(ProjectPath); Directory.CreateDirectory(Group); Directory.CreateDirectory(ProjectsRoot);
        config = new AppConfig
        {
            DeviceId = name, DesktopRoot = Path.Combine(device, "desktop"), AccountId = "acc", WorkspaceId = "ws", ProjectsRoot = ProjectsRoot,
            Projects = [new ProjectMap { Id = ProjectId, Name = "proj", Path = ProjectPath }]
        };
    }

    public SyncEngine Engine() => new(config, cloud, new LocalClaude(config), Home, () => new ClaudeSnapshot(Processes()))
        { UtcNow = () => now };
    private List<ClaudeProcess> Processes()
    {
        var list = new List<ClaudeProcess>();
        if (DesktopRunning) list.Add(new(1, ClaudeProcessKind.Desktop, "", null));
        if (SessionOpen) list.Add(new(2, ClaudeProcessKind.DesktopSession, "", current));
        return list;
    }

    public void Subscribe() => config.Subscriptions.Add(new Subscription { SessionId = SessionId, ProjectId = ProjectId, CliSessionId = CliId });
    public async Task<SessionReport> Sync() => (await Engine().SyncAll()).Single();
    public void Age(TimeSpan quiet) { now = DateTime.UtcNow + quiet; }

    public string Line(string id, string parent, string? cwd = null)
        => new JsonObject { ["parentUuid"] = parent.Length == 0 ? null : parent, ["type"] = "user", ["uuid"] = id, ["cwd"] = cwd ?? ProjectPath, ["sessionId"] = current,
            ["timestamp"] = DateTime.UtcNow.ToString("O"), ["message"] = new JsonObject { ["role"] = "user", ["content"] = "hello " + id } }.ToJsonString();

    public void CreateConversation(params string[] ids)
    {
        var meta = new JsonObject { ["sessionId"] = DesktopId, ["cliSessionId"] = CliId, ["cwd"] = ProjectPath, ["originCwd"] = ProjectPath, ["title"] = "Test", ["isArchived"] = false,
            ["bridgeSessionIds"] = new JsonArray("bridge-" + Name) };
        File.WriteAllText(MetaPath, meta.ToJsonString());
        Directory.CreateDirectory(Path.GetDirectoryName(TranscriptPath)!);
        File.WriteAllText(TranscriptPath, "");
        Append(ids);
    }
    public void Append(params string[] ids)
    {
        if (lastId.Length == 0 && File.Exists(TranscriptPath))
            lastId = File.ReadAllLines(TranscriptPath).Select(l => { try { return JsonNode.Parse(l)?["uuid"]?.ToString(); } catch { return null; } }).LastOrDefault(x => x is not null) ?? "";
        foreach (var id in ids) { AppendRaw(Line(id, lastId)); lastId = id; }
    }
    public void AppendRaw(string line, bool newline = true)
    {
        File.AppendAllText(TranscriptPath, line + (newline ? "\n" : ""));
        File.SetLastWriteTimeUtc(TranscriptPath, DateTime.UtcNow);
        now = DateTime.UtcNow.AddHours(1);
    }
    public void OpenInClaude()
    {
        foreach (var line in new[] { "{\"type\":\"custom-title\",\"customTitle\":\"Test\",\"sessionId\":\"" + CliId + "\"}",
                     "{\"type\":\"mode\",\"mode\":\"normal\",\"sessionId\":\"" + CliId + "\"}",
                     "{\"type\":\"bridge-session\",\"bridgeSessionId\":\"" + Guid.NewGuid() + "\",\"sessionId\":\"" + CliId + "\"}" })
            AppendRaw(line);
    }
    public string TranscriptText() => File.ReadAllText(TranscriptPath);
    public JsonObject Meta() => (JsonObject)JsonNode.Parse(File.ReadAllText(MetaPath))!;

    /// <summary>What Claude Desktop does on rewind: keep the old transcript only up to the fork point, start a new
    /// transcript that repeats that history, and point the conversation's metadata at it.</summary>
    public void Rewind(string newCli, string forkPoint, params string[] newIds)
    {
        var old = current;
        var kept = new List<string>();
        foreach (var line in File.ReadAllLines(TranscriptPath))
        {
            kept.Add(line);
            if (JsonNode.Parse(line)?["uuid"]?.ToString() == forkPoint) break;
        }
        File.WriteAllLines(TranscriptPath, kept);
        current = newCli;
        File.WriteAllLines(TranscriptPath, kept.Where(l => JsonNode.Parse(l)?["uuid"] is not null));
        var meta = Meta();
        meta["cliSessionId"] = newCli;
        meta["priorCliSessionIds"] = new JsonArray(old);
        meta["rewindEdges"] = new JsonArray(new JsonObject { ["parent"] = old, ["child"] = newCli, ["cwd"] = ProjectPath, ["forkPoint"] = forkPoint });
        File.WriteAllText(MetaPath, meta.ToJsonString());
        lastId = forkPoint;
        Append(newIds);
    }

    /// <summary>After receiving a rewound conversation this device writes to its new transcript.</summary>
    public void Follow(string cli) { current = cli; lastId = ""; }
    public byte[] PackLikeVersion1() => Package.Pack(File.ReadAllBytes(MetaPath), File.ReadAllBytes(TranscriptPath), ProjectPath, "", "");
}

internal sealed class FakeCloud : ICloudApi
{
    private sealed class Session { public string Head = ""; public string Cli = ""; public readonly List<JsonObject> Versions = []; }
    private readonly Dictionary<string, Session> sessions = [];
    private readonly Dictionary<string, byte[]> blobs = [];
    public int VersionCount => sessions.Values.Sum(x => x.Versions.Count);
    public int Downloads { get; private set; }
    public string Head(string id) => sessions[id].Head;
    public void AddSession(string id, string cli) => sessions[id] = new Session { Cli = cli };
    public void Remove(string id) => sessions.Remove(id);

    public Task<JsonNode> Library()
    {
        var list = new JsonArray();
        foreach (var (id, s) in sessions)
            list.Add(new JsonObject { ["id"] = id, ["head_version_id"] = s.Head.Length == 0 ? null : s.Head, ["cli_session_id"] = s.Cli, ["desktop_session_id"] = Device.DesktopId, ["project_id"] = "p1", ["title"] = "Test" });
        return Task.FromResult<JsonNode>(new JsonObject { ["sessions"] = list, ["projects"] = new JsonArray(), ["devices"] = new JsonArray(), ["links"] = new JsonArray() });
    }
    public Task<JsonArray> Versions(string sessionId) => Task.FromResult(new JsonArray(sessions[sessionId].Versions.Select(v => (JsonNode)v.DeepClone()).ToArray()));
    public Task<byte[]> Download(string versionId) { Downloads++; return Task.FromResult(blobs[versionId]); }
    public Task<string> Upload(string sessionId, byte[] package, string deviceId, string expectedHead, string transcriptHash)
    {
        var s = sessions[sessionId];
        if (expectedHead != s.Head) throw new CloudException("head moved", HttpStatusCode.Conflict);
        Package.Unpack(package);
        var id = Guid.NewGuid().ToString();
        blobs[id] = package;
        s.Versions.Add(new JsonObject { ["id"] = id, ["package_hash"] = Transcript.Hash(package), ["transcript_hash"] = transcriptHash,
            ["created_at"] = DateTimeOffset.UtcNow.ToString("O"), ["device_id"] = deviceId });
        s.Head = id;
        return Task.FromResult(id);
    }
}
