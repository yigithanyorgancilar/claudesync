using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClaudeSync;

public enum Tone { Neutral, Success, Attention, Warning, Error }

/// <summary>One conversation row in the main window, and the model behind the detail pane.</summary>
public sealed class ConversationItem(string id) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly Dictionary<string, object?> values = [];
    private T Get<T>(T fallback = default!, [CallerMemberName] string name = "") => values.TryGetValue(name, out var v) ? (T)v! : fallback;
    private void Set<T>(T value, [CallerMemberName] string name = "")
    {
        if (values.TryGetValue(name, out var old) && Equals(old, value)) return;
        values[name] = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public string Id { get; } = id;
    public string ProjectId { get => Get(""); set => Set(value); }
    public string CliId { get => Get(""); set => Set(value); }
    public string Title { get => Get(""); set => Set(value); }
    public string ProjectName { get => Get(""); set => Set(value); }
    public string? LocalPath { get => Get<string?>(null); set => Set(value); }
    public bool IsLinked { get => Get(false); set { Set(value); Set(value ? L.SectionLinked : L.SectionCloud, nameof(Section)); } }
    public string Section => Get("");

    public Tone Tone { get => Get(Tone.Neutral); set => Set(value); }
    public string Glyph { get => Get("\uE753"); set => Set(value); }
    public string StatusShort { get => Get(""); set { Set(value); Set($"{ProjectName}  ·  {value}", nameof(Subtitle)); } }
    public string Subtitle => Get("");
    public string StatusTitle { get => Get(""); set => Set(value); }
    public string StatusDetail { get => Get(""); set => Set(value); }
    public string LastSync { get => Get(L.NeverSynced); set => Set(value); }

    public bool CanJoin { get => Get(false); set => Set(value); }
    public bool IsConflict { get => Get(false); set => Set(value); }
    public bool IsPending { get => Get(false); set => Set(value); }
    public bool NeedsFolder { get => Get(false); set => Set(value); }
    public bool HasFolder { get => Get(false); set => Set(value); }

    /// <summary>Fills the status fields from the latest sync result.</summary>
    public void Apply(bool linked, SessionReport? report, SyncBase? baseline)
    {
        IsLinked = linked;
        CanJoin = !linked;
        IsConflict = linked && report?.Status == SyncStatus.Conflict;
        IsPending = linked && report?.Status == SyncStatus.PendingDownload;
        NeedsFolder = linked && report?.Status == SyncStatus.NeedsFolder;
        HasFolder = linked && LocalPath is not null && Directory.Exists(LocalPath);
        LastSync = baseline?.SyncedAt is { } at ? Text.Relative(at) : L.NeverSynced;

        (Tone tone, string glyph, string shortText, string title, string detail) = !linked
            ? (Tone.Neutral, "\uE753", L.InCloud, L.NotOnThisComputer, L.NotOnThisComputerDetail)
            : report?.Status switch
            {
                null => (Tone.Neutral, "\uE823", L.Waiting, L.Waiting, L.WaitingDetail),
                SyncStatus.UpToDate => (Tone.Success, "\uE73E", L.UpToDate, L.UpToDate, L.UpToDateDetail),
                SyncStatus.Uploaded => (Tone.Success, "\uE898", L.Uploaded, L.Uploaded, L.UploadedDetail),
                SyncStatus.Downloaded => (Tone.Success, "\uE896", L.Downloaded, L.Downloaded, L.DownloadedDetail),
                SyncStatus.PendingDownload => (Tone.Attention, "\uE896", L.PendingShort, L.PendingTitle,
                    (report.RemoteOnly > 0 ? L.NewEntries(report.RemoteOnly) : "") + report.Message),
                SyncStatus.WaitingIdle => (Tone.Neutral, "\uE916", L.InProgressShort, L.InProgressTitle, report.Message),
                SyncStatus.Conflict => (Tone.Warning, "\uE7BA", L.ConflictShort, L.ConflictTitle, L.ConflictDetail(report.LocalOnly, report.RemoteOnly)),
                SyncStatus.NeedsFolder => (Tone.Warning, "\uE8B7", L.NeedsFolderShort, L.NeedsFolderTitle, report.Message),
                SyncStatus.MissingLocal => (Tone.Warning, "\uE783", L.NotFoundShort, L.NotFoundTitle, report.Message),
                SyncStatus.MissingCloud => (Tone.Error, "\uE783", L.NotInCloudShort, L.NotInCloudTitle, report.Message),
                SyncStatus.Retry => (Tone.Neutral, "\uE895", L.RetryShort, L.RetryShort, report.Message),
                _ => (Tone.Error, "\uEA39", L.ErrorShort, L.ErrorTitle, report.Message),
            };
        Tone = tone; Glyph = glyph; StatusShort = shortText; StatusTitle = title; StatusDetail = detail;
    }
}

public static class Text
{
    public static string Relative(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var span = DateTimeOffset.Now - local;
        if (span < TimeSpan.FromMinutes(1)) return L.JustNow;
        if (span < TimeSpan.FromHours(1)) return L.MinutesAgo((int)span.TotalMinutes);
        if (local.Date == DateTime.Today) return L.Today(local.ToString("HH:mm", L.Culture));
        if (local.Date == DateTime.Today.AddDays(-1)) return L.Yesterday(local.ToString("HH:mm", L.Culture));
        return local.ToString(local.Year == DateTime.Now.Year ? "d MMM HH:mm" : "d MMM yyyy", L.Culture);
    }

    public static string Date(DateTimeOffset when) => when.ToLocalTime().ToString(L.Turkish ? "d MMMM yyyy, HH:mm" : "MMMM d, yyyy, HH:mm", L.Culture);
}

/// <summary>Where ClaudeSync lives on the internet.</summary>
public static class Links
{
    public const string Repository = "https://github.com/yigithanyorgancilar/claudesync";
    public const string SetupGuide = Repository + "/blob/main/docs/self-hosting.md";
    public const string Donate = "https://buymeacoffee.com/yigithanyorgancilar";

    public static void Open(string url) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
}
