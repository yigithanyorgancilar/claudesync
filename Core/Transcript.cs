using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeSync;

/// <summary>
/// A Claude Code transcript (.jsonl). Conversation entries carry a top-level "uuid"; Claude also appends
/// uuid-less bookkeeping lines (custom-title, mode, bridge-session, last-prompt …) every time a session is
/// merely opened. Sync decisions compare the uuid sets so that opening a conversation never counts as a change.
/// </summary>
public sealed class Transcript
{
    public byte[] Bytes { get; }
    public HashSet<string> Ids { get; }
    public DateTimeOffset? LastEntryAt { get; }
    public string Sig { get; }

    private Transcript(byte[] bytes, HashSet<string> ids, DateTimeOffset? last)
    {
        Bytes = bytes; Ids = ids; LastEntryAt = last;
        Sig = Signature(ids);
    }

    /// <summary>Order-independent fingerprint of a set of entry ids.</summary>
    public static string Signature(IEnumerable<string> ids)
        => Hash(Encoding.UTF8.GetBytes(string.Join('\n', ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))));

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <param name="trimPartialTail">Drop a final line that is still being written (no newline, not valid JSON).</param>
    public static Transcript Parse(byte[] bytes, bool trimPartialTail = false)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? last = null;
        var keep = bytes.Length;
        var start = 0;
        while (start < bytes.Length)
        {
            var rel = bytes.AsSpan(start).IndexOf((byte)'\n');
            var end = rel < 0 ? bytes.Length : start + rel;
            var line = bytes.AsSpan(start, end - start);
            if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
            if (line.Trim((byte)' ').Length > 0)
            {
                if (TryRead(line, out var uuid, out var stamp))
                {
                    if (uuid is not null) ids.Add(uuid);
                    if (stamp is not null && (last is null || stamp > last)) last = stamp;
                }
                else if (rel < 0 && trimPartialTail) { keep = start; break; }
            }
            if (rel < 0) break;
            start = end + 1;
        }
        return new Transcript(keep == bytes.Length ? bytes : bytes[..keep], ids, last);
    }

    private static bool TryRead(ReadOnlySpan<byte> line, out string? uuid, out DateTimeOffset? stamp)
    {
        uuid = null; stamp = null;
        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isUuid = reader.ValueTextEquals("uuid"u8);
                var isStamp = reader.ValueTextEquals("timestamp"u8);
                reader.Read();
                if (isUuid && reader.TokenType == JsonTokenType.String) uuid = reader.GetString();
                else if (isStamp && reader.TokenType == JsonTokenType.String
                    && DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) stamp = date;
                else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.CurrentDepth != 0) return false;
            return !reader.Read();
        }
        catch (JsonException) { return false; }
    }

    // ---- Legacy content hash -------------------------------------------------------------------------
    // The server stores, and every client verifies, SHA-256 of the transcript with lines whose cwd/originCwd
    // equal the source folder rewritten to a placeholder. This must stay bit-for-bit what version 1 did.

    public const string Placeholder = "/claudesync/project";
    private static readonly Regex LineSplit = new(".*?(?:\\r\\n|\\n|$)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly string[] CwdKeys = ["cwd", "originCwd"];

    public static string LegacyHash(byte[] transcript, string cwd) => Hash(RewriteLines(transcript, value => value == cwd ? Placeholder : null));

    /// <summary>Moves a transcript to another project folder, including sub-folder cwd values.</summary>
    public static byte[] Relocate(byte[] transcript, string source, string target)
        => RewriteLines(transcript, value => RelocatePath(value, source, target) is var moved && moved != value ? moved : null);

    public static string RelocatePath(string value, string source, string target)
    {
        source = source.TrimEnd('\\', '/'); target = target.TrimEnd('\\', '/');
        if (source.Length == 0) return value;
        if (value.Equals(source, StringComparison.OrdinalIgnoreCase)) return target;
        if (value.Length > source.Length && value.StartsWith(source, StringComparison.OrdinalIgnoreCase) && value[source.Length] is '\\' or '/')
            return target + value[source.Length..];
        return value;
    }

    private static byte[] RewriteLines(byte[] data, Func<string, string?> rewrite)
    {
        var text = Encoding.UTF8.GetString(data);
        var output = new StringBuilder(text.Length);
        foreach (Match match in LineSplit.Matches(text))
        {
            var line = match.Value;
            if (line.Length == 0) continue;
            var ending = line.EndsWith("\r\n") ? "\r\n" : line.EndsWith('\n') ? "\n" : "";
            var core = ending.Length == 0 ? line : line[..^ending.Length];
            if (core.Length == 0 || !(core.Contains("\"cwd\"") || core.Contains("\"originCwd\""))) { output.Append(core).Append(ending); continue; }
            var node = JsonNode.Parse(core) ?? throw new InvalidDataException("Invalid transcript line");
            var changed = false;
            if (node is JsonObject obj)
                foreach (var key in CwdKeys)
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var current) && rewrite(current) is { } next)
                    { obj[key] = next; changed = true; }
            output.Append(changed ? node.ToJsonString(Store.CompactJson) : core).Append(ending);
        }
        return Encoding.UTF8.GetBytes(output.ToString());
    }
}

/// <summary>
/// The zip shared with version 1: manifest.json, session.json, transcript.jsonl — plus, since 2.1, one
/// prior/&lt;cliSessionId&gt;.jsonl per earlier transcript of the conversation. Claude Desktop starts a new
/// transcript when a conversation is rewound and keeps the old ones listed in priorCliSessionIds.
/// </summary>
public sealed record Package(JsonNode Manifest, byte[] MetaBytes, JsonNode Meta, byte[] TranscriptBytes, IReadOnlyDictionary<string, byte[]> Prior)
{
    public const string Format = "claudesync-poc";
    public string SessionId => Manifest["sessionId"]?.ToString() ?? "";
    public string CliSessionId => Manifest["cliSessionId"]?.ToString() ?? "";
    public string SourceCwd => Manifest["sourceCwd"]?.ToString() ?? "";
    public string Replaces => Manifest["replaces"]?.ToString() ?? "";

    /// <summary>Every transcript of the conversation, current first.</summary>
    public IEnumerable<byte[]> Transcripts => Prior.Values.Prepend(TranscriptBytes);

    private static readonly Regex PriorEntry = new(@"^prior/(?<id>[0-9a-fA-F-]{36})\.jsonl$", RegexOptions.Compiled);

    /// <param name="replaces">Cloud version the user deliberately overrode (conflict resolved with "keep this device").</param>
    public static byte[] Pack(byte[] metaBytes, byte[] transcript, string cwd, string projectSlug, string parentHash,
        string? replaces = null, IReadOnlyDictionary<string, byte[]>? prior = null)
    {
        var meta = JsonNode.Parse(metaBytes) ?? throw new InvalidDataException("Empty session metadata");
        var hashes = new JsonObject { ["session.json"] = Transcript.Hash(metaBytes), ["transcript.jsonl"] = Transcript.Hash(transcript) };
        foreach (var (id, bytes) in prior ?? new Dictionary<string, byte[]>()) hashes[$"prior/{id}.jsonl"] = Transcript.Hash(bytes);
        var manifest = new JsonObject
        {
            ["format"] = Format, ["version"] = 1, ["sessionId"] = meta["sessionId"]?.ToString(),
            ["cliSessionId"] = meta["cliSessionId"]?.ToString(), ["sourceCwd"] = cwd,
            ["sourceProjectSlug"] = projectSlug,
            ["parentHash"] = parentHash.Length == 64 ? parentHash : new string('0', 64),
            ["sha256"] = hashes
        };
        if (replaces is { Length: > 0 }) manifest["replaces"] = replaces;
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            static void Put(ZipArchive z, string name, byte[] content) { using var s = z.CreateEntry(name, CompressionLevel.Optimal).Open(); s.Write(content); }
            Put(zip, "manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            Put(zip, "session.json", metaBytes);
            Put(zip, "transcript.jsonl", transcript);
            foreach (var (id, bytes) in prior ?? new Dictionary<string, byte[]>()) Put(zip, $"prior/{id}.jsonl", bytes);
        }
        return stream.ToArray();
    }

    public static Package Unpack(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var names = zip.Entries.Select(x => x.FullName).ToArray();
        string[] required = ["manifest.json", "session.json", "transcript.jsonl"];
        if (required.Any(x => !names.Contains(x)) || names.Except(required).Any(x => !PriorEntry.IsMatch(x)) || names.Distinct().Count() != names.Length)
            throw new InvalidDataException(L.InvalidPackage);
        byte[] Read(string name)
        {
            var entry = zip.GetEntry(name)!;
            if (entry.Length > 500L * 1024 * 1024) throw new InvalidDataException(L.PackageTooLarge);
            using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
        }
        var manifest = JsonNode.Parse(Read("manifest.json")) ?? throw new InvalidDataException(L.EmptyManifest);
        var metaBytes = Read("session.json");
        var transcript = Read("transcript.jsonl");
        string P(JsonNode? n, string k) => n?[k]?.ToString() ?? "";
        if (P(manifest, "format") != Format || P(manifest, "version") != "1"
            || P(manifest["sha256"], "session.json") != Transcript.Hash(metaBytes)
            || P(manifest["sha256"], "transcript.jsonl") != Transcript.Hash(transcript)) throw new InvalidDataException(L.PackageNotVerified);
        var prior = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.Where(x => PriorEntry.IsMatch(x)))
        {
            var content = Read(name);
            if (P(manifest["sha256"], name) != Transcript.Hash(content)) throw new InvalidDataException(L.PackageNotVerified);
            prior[PriorEntry.Match(name).Groups["id"].Value] = content;
        }
        var meta = JsonNode.Parse(metaBytes) ?? throw new InvalidDataException(L.EmptySession);
        if (P(meta, "sessionId") != P(manifest, "sessionId") || P(meta, "cliSessionId") != P(manifest, "cliSessionId")
            || !Guid.TryParse(P(meta, "cliSessionId"), out _)) throw new InvalidDataException(L.PackageOtherConversation);
        return new Package(manifest, metaBytes, meta, transcript, prior);
    }
}
