using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClaudeSync;

public partial class MainWindow : Window
{
    private AppConfig config;
    private CloudClient cloud;
    private LocalClaude claude;
    private SyncEngine engine;
    private readonly bool demo;
    private readonly ObservableCollection<ConversationItem> items = [];
    private readonly ListCollectionView view;
    /// <summary>Held while syncing or while the config is being changed, so the two never overlap.</summary>
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly DispatcherTimer syncTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer watchTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly TrayIcon? tray;
    private readonly ConversationItem banner = new("banner");
    private readonly HashSet<string> notified = [];
    private Action? bannerAction;
    private ClaudeSnapshot snapshot = ClaudeSnapshot.Empty;
    private JsonNode? library;
    private string? cloudError;
    private string listShape = "";
    private bool cloudUnreachable;
    private DateTimeOffset? lastCheck;
    private bool syncing, exiting, trayHintShown;

    private bool SignedIn => demo || (config.ServerConfigured && cloud.HasSession && config.DeviceId.Length > 0);
    private ConversationItem? Selected => Conversations.SelectedItem as ConversationItem;
    private static string P(JsonNode? node, string key) => node?[key]?.ToString() ?? "";

    public MainWindow(bool demo)
    {
        this.demo = demo;
        InitializeComponent();
        config = demo ? new AppConfig() : Store.Load<AppConfig>(Store.ConfigPath);
        CreateServices();
        view = (ListCollectionView)CollectionViewSource.GetDefaultView(items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ConversationItem.Section)));
        view.SortDescriptions.Add(new SortDescription(nameof(ConversationItem.IsLinked), ListSortDirection.Descending));
        view.SortDescriptions.Add(new SortDescription(nameof(ConversationItem.Title), ListSortDirection.Ascending));
        view.Filter = o => o is ConversationItem item && (Search.Text.Length == 0
            || item.Title.Contains(Search.Text, StringComparison.CurrentCultureIgnoreCase)
            || item.ProjectName.Contains(Search.Text, StringComparison.CurrentCultureIgnoreCase));
        Conversations.ItemsSource = view;
        Banner.DataContext = banner;
        DeviceName.Text = Environment.MachineName;
        Loaded += (_, _) =>
        {
            if (previewIndex is { } index && index < view.Count) Conversations.SelectedItem = view.GetItemAt(index);
            else if (Selected is null && !view.IsEmpty) Conversations.SelectedIndex = 0;
        };
        if (demo) { LoadDemo(); return; }

        tray = new TrayIcon(ShowWindow, () => _ = SyncNow(), () => _ = CloseClaudeAndApply(), ExitApplication);
        syncTimer.Tick += async (_, _) => await SyncNow();
        watchTimer.Tick += async (_, _) => await Watch();
        Loaded += async (_, _) =>
        {
            UpdateAccount();
            await Watch();
            if (SignedIn) await SyncNow();
        };
        syncTimer.Start();
        watchTimer.Start();
    }

    [MemberNotNull(nameof(cloud), nameof(claude), nameof(engine))]
    private void CreateServices()
    {
        cloud = new CloudClient(config);
        claude = new LocalClaude(config);
        engine = new SyncEngine(config, cloud, claude, Store.Home);
    }

    private void Save() => Store.Save(Store.ConfigPath, config);

    // ---- window & tray ---------------------------------------------------------------------------------

    public void ShowWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void ExitApplication()
    {
        exiting = true;
        syncTimer.Stop(); watchTimer.Stop();
        tray?.Dispose();
        Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!exiting && !demo)
        {
            e.Cancel = true;
            Hide();
            if (!trayHintShown) { trayHintShown = true; tray?.Notify(L.TrayRunningTitle, L.TrayRunningText); }
        }
        base.OnClosing(e);
    }

    // ---- sync ------------------------------------------------------------------------------------------

    private async Task Watch()
    {
        if (demo) return;
        var wasRunning = snapshot.DesktopRunning;
        try { snapshot = await Task.Run(ClaudeProcesses.Snapshot); } catch (Exception ex) { Store.Log("Process scan: " + ex.Message); }
        UpdateFooter();
        UpdateBanner();
        // Claude was just closed: apply waiting downloads right away instead of at the next tick.
        if (wasRunning && !snapshot.DesktopRunning && items.Any(x => x.IsPending)) await SyncNow();
    }

    private async Task SyncNow()
    {
        if (!SignedIn || demo || !await gate.WaitAsync(0)) return;
        try { await SyncCore(); }
        finally { gate.Release(); }
    }

    /// <summary>Caller holds <see cref="gate"/>.</summary>
    private async Task SyncCore(Func<Task<IReadOnlyList<SessionReport>>>? work = null)
    {
        SetSyncing(true);
        try
        {
            var reports = await Task.Run(work ?? (() => engine.SyncAll()));
            cloudError = null;
            Notify(reports);
            // Removed from the cloud on another computer: stop syncing it here too.
            foreach (var removed in reports.Where(x => x.Status == SyncStatus.MissingCloud)) Forget(removed.SessionId);
        }
        catch (Exception ex) { HandleCloudError(ex); }
        try { library = await cloud.Library(); }
        catch (Exception ex) { HandleCloudError(ex); }
        lastCheck = DateTimeOffset.Now;
        SetSyncing(false);
        Render();
    }

    private void HandleCloudError(Exception ex)
    {
        Store.Log("Sync: " + ex);
        cloudError = ex.Message;
        cloudUnreachable = ex is CloudException;
        if (ex is CloudException { Status: HttpStatusCode.Unauthorized }) { cloud.SignOut(); UpdateAccount(); }
    }

    private void SetSyncing(bool value)
    {
        syncing = value;
        SyncButton.IsEnabled = !value;
        SyncButtonText.Text = value ? L.Syncing : L.SyncNow;
        var spin = (Storyboard)FindResource("Spin");
        if (value) spin.Begin(this, true); else spin.Stop(this);
        UpdateFooter();
    }

    private void Notify(IEnumerable<SessionReport> reports)
    {
        if (tray is null) return;
        foreach (var report in reports)
        {
            var title = items.FirstOrDefault(x => x.Id == report.SessionId)?.Title ?? L.Conversation;
            var key = $"{report.SessionId}:{report.Status}:{report.RemoteHead ?? report.Message}";
            switch (report.Status)
            {
                case SyncStatus.Downloaded:
                    tray.Notify(L.NotifyUpdated(title), L.NotifyUpdatedText);
                    break;
                case SyncStatus.PendingDownload when notified.Add(key):
                    tray.Notify(L.NotifyPending, L.NotifyPendingText(title));
                    break;
                case SyncStatus.Conflict when notified.Add(key):
                    tray.Notify(L.NotifyConflict, L.NotifyConflictText(title), System.Windows.Forms.ToolTipIcon.Warning);
                    break;
                case SyncStatus.MissingCloud:
                    tray.Notify(L.NotifyRemoved(title), L.NotifyRemovedText);
                    break;
                case SyncStatus.Error when notified.Add(key):
                    tray.Notify(L.FailedOne(title), report.Message, System.Windows.Forms.ToolTipIcon.Error);
                    break;
            }
        }
    }

    // ---- rendering -------------------------------------------------------------------------------------

    private void UpdateAccount()
    {
        var signedIn = SignedIn;
        Welcome.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        ServerPanel.Visibility = config.ServerConfigured || demo ? Visibility.Collapsed : Visibility.Visible;
        SignInPanel.Visibility = config.ServerConfigured || demo ? Visibility.Visible : Visibility.Collapsed;
        Library.Visibility = HeaderActions.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        if (!signedIn) Banner.Visibility = Visibility.Collapsed;
        AccountName.Text = config.Email is { Length: > 0 } email ? email : L.GoogleAccount;
        AccountInitial.Text = AccountName.Text[..1].ToUpperInvariant();
    }

    private void Render()
    {
        if (library is not null)
        {
            var state = engine.LoadState();
            var projects = (library["projects"]?.AsArray() ?? []).Where(x => x is not null).GroupBy(x => P(x, "id")).ToDictionary(g => g.Key, g => P(g.First(), "name"));
            var linked = config.Subscriptions.Select(x => x.SessionId).ToHashSet();
            var fresh = new List<ConversationItem>();
            foreach (var session in library["sessions"]?.AsArray() ?? [])
            {
                var id = P(session, "id");
                if (id.Length == 0 || fresh.Any(x => x.Id == id)) continue;
                var item = items.FirstOrDefault(x => x.Id == id) ?? new ConversationItem(id);
                item.ProjectId = P(session, "project_id");
                item.CliId = P(session, "cli_session_id");
                item.Title = P(session, "title") is { Length: > 0 } title ? title : L.Untitled;
                item.ProjectName = projects.GetValueOrDefault(item.ProjectId) is { Length: > 0 } name ? name : L.Project;
                item.LocalPath = config.Projects.FirstOrDefault(x => x.Id == item.ProjectId)?.Path;
                item.Apply(linked.Contains(id), state.Reports.GetValueOrDefault(id), state.Base.GetValueOrDefault(id));
                fresh.Add(item);
            }
            foreach (var sub in config.Subscriptions.Where(x => fresh.All(i => i.Id != x.SessionId)))
            {
                var item = items.FirstOrDefault(x => x.Id == sub.SessionId) ?? new ConversationItem(sub.SessionId) { Title = L.UnknownConversation };
                item.ProjectId = sub.ProjectId;
                item.CliId = sub.CliSessionId;
                item.Apply(true, state.Reports.GetValueOrDefault(sub.SessionId), state.Base.GetValueOrDefault(sub.SessionId));
                fresh.Add(item);
            }
            Show(fresh);
        }
        if (Selected is null && !view.IsEmpty) Conversations.SelectedIndex = 0;
        UpdateDetail();
        UpdateBanner();
        UpdateFooter();
        tray?.SetStatus(items.Any(x => x.Tone is Tone.Warning or Tone.Error) ? L.TrayAttention : "ClaudeSync · " + FooterStatus.Text);
    }

    /// <summary>Replaces the list contents, keeping the selection; regroups only when membership, section or title changed.</summary>
    private void Show(IReadOnlyList<ConversationItem> fresh)
    {
        var shape = string.Join('\n', fresh.Select(x => $"{x.Id}|{x.IsLinked}|{x.Title}"));
        if (shape == listShape) return;
        listShape = shape;
        var selected = Selected;
        foreach (var gone in items.Except(fresh).ToArray()) items.Remove(gone);
        foreach (var added in fresh.Except(items).ToArray()) items.Add(added);
        view.Refresh(); // existing rows may have moved between sections
        if (selected is not null && items.Contains(selected)) Conversations.SelectedItem = selected;
    }

    private void UpdateDetail()
    {
        var item = Selected;
        Detail.DataContext = item;
        Detail.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyDetail.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        var empty = items.Count == 0;
        EmptyGlyph.Text = empty ? "\uE8F2" : "\uE8BD";
        EmptyTitle.Text = empty ? L.NoConversations : L.SelectConversation;
        EmptyText.Text = empty ? L.NoConversationsHint : L.SelectConversationHint;
        EmptyAction.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBanner()
    {
        var pending = items.Where(x => x.IsPending).ToArray();
        var conflicts = items.Where(x => x.IsConflict).ToArray();
        var errors = items.Where(x => x.IsLinked && x.Tone == Tone.Error).ToArray();
        if (!SignedIn) HideBanner();
        else if (cloudError is not null)
            ShowBanner(Tone.Error, "\uEB55", cloudUnreachable ? L.CloudUnreachable : L.SyncFailed, cloudError, L.TryAgain, () => _ = SyncNow());
        else if (pending.Length > 0 && snapshot.DesktopRunning)
            ShowBanner(Tone.Attention, "\uE896",
                pending.Length == 1 ? L.ContinuedElsewhere(pending[0].Title) : L.NewMessagesIn(pending.Length),
                L.DesktopOpenBanner, L.CloseClaudeAndApply, () => _ = CloseClaudeAndApply());
        else if (pending.Length > 0)
            ShowBanner(Tone.Attention, "\uE896", L.PendingApply, pending[0].StatusDetail, L.ApplyNow, () => _ = SyncNow());
        else if (conflicts.Length > 0)
            ShowBanner(Tone.Warning, "\uE7BA",
                conflicts.Length == 1 ? L.ConflictBanner(conflicts[0].Title) : L.ConflictBannerMany(conflicts.Length),
                L.ConflictBannerDetail, L.Show, () => Conversations.SelectedItem = conflicts[0]);
        else if (errors.Length > 0)
            ShowBanner(Tone.Error, "\uEA39", errors.Length == 1 ? L.FailedOne(errors[0].Title) : L.FailedMany(errors.Length),
                errors[0].StatusDetail, L.Show, () => Conversations.SelectedItem = errors[0]);
        else HideBanner();
    }

    private void ShowBanner(Tone tone, string glyph, string title, string detail, string action, Action onAction)
    {
        (banner.Tone, banner.Glyph, banner.StatusTitle, banner.StatusDetail) = (tone, glyph, title, detail);
        BannerAction.Content = action;
        bannerAction = onAction;
        Banner.Visibility = Visibility.Visible;
    }

    private void HideBanner() { Banner.Visibility = Visibility.Collapsed; bannerAction = null; }

    private void UpdateFooter()
    {
        ClaudeState.Text = snapshot.DesktopRunning ? L.DesktopOpen : L.DesktopClosed;
        ClaudeDot.Fill = (System.Windows.Media.Brush)FindResource(snapshot.DesktopRunning ? "AccentFillColorDefaultBrush" : "TextFillColorTertiaryBrush");
        FooterStatus.Text = !SignedIn ? L.FooterSignedOut
            : syncing ? L.FooterChecking
            : lastCheck is { } at ? L.FooterLastCheck(Text.Relative(at))
            : L.FooterAuto;
    }

    // ---- actions ---------------------------------------------------------------------------------------

    /// <summary>Runs a config-changing action under the sync gate and reports failures.</summary>
    private async Task Guarded(Func<Task> action, string failureTitle)
    {
        await gate.WaitAsync();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Store.Log(failureTitle + ": " + ex);
            Dialog.Info(this, failureTitle, ex.Message, Tone.Error);
        }
        finally { gate.Release(); }
    }

    private async void Sync_Click(object sender, RoutedEventArgs e) => await SyncNow();
    private void BannerAction_Click(object sender, RoutedEventArgs e) => bannerAction?.Invoke();
    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        view.Refresh();
    }
    private void Conversations_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetail();

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!SignedIn || demo) return;
        IReadOnlyList<LocalSession> sessions;
        try { sessions = claude.Sessions(); }
        catch (Exception ex) { Dialog.Info(this, L.CannotReadClaude, ex.Message, Tone.Error); return; }
        // A conversation keeps its Desktop id when a rewind gives it a new transcript id.
        var subscribed = config.Subscriptions.Select(x => x.SessionId).ToHashSet();
        var takenDesktop = (library?["sessions"]?.AsArray() ?? []).Where(x => subscribed.Contains(P(x, "id"))).Select(x => P(x, "desktop_session_id")).ToHashSet();
        bool Taken(LocalSession x) => takenDesktop.Contains(x.DesktopId) || config.Subscriptions.Any(s => x.Owns(s.CliSessionId));
        var picker = new AddConversationWindow(sessions.Where(x => x.TranscriptPath is not null && !Taken(x)).ToArray()) { Owner = this };
        if (picker.ShowDialog() != true || picker.Chosen.Count == 0) return;

        await Guarded(async () =>
        {
            var current = await cloud.Library();
            var cloudSessions = current["sessions"]?.AsArray() ?? [];
            var cloudProjects = current["projects"]?.AsArray() ?? [];
            foreach (var local in picker.Chosen)
            {
                var existing = cloudSessions.FirstOrDefault(x => P(x, "desktop_session_id") == local.DesktopId || local.Owns(P(x, "cli_session_id")));
                var projectId = existing is not null ? P(existing, "project_id")
                    : config.Projects.FirstOrDefault(x => SamePath(x.Path, local.Cwd))?.Id
                    ?? cloudProjects.Where(x => P(x, "name").Equals(local.ProjectName, StringComparison.OrdinalIgnoreCase))
                        .Select(x => P(x, "id")).FirstOrDefault(id => !config.Projects.Any(m => m.Id == id))
                    ?? await cloud.CreateProject(local.ProjectName);
                if (!config.Projects.Any(x => x.Id == projectId))
                    config.Projects.Add(new ProjectMap { Id = projectId, Name = local.ProjectName, Path = local.Cwd });
                var sessionId = existing is not null ? P(existing, "id") : await cloud.CreateSession(projectId, local.CliId, local.DesktopId, local.Title);
                await cloud.Link(config.DeviceId, sessionId);
                if (!config.Subscriptions.Any(x => x.SessionId == sessionId))
                    config.Subscriptions.Add(new Subscription { SessionId = sessionId, ProjectId = projectId, CliSessionId = local.CliId });
                Save();
            }
            await SyncCore();
            if (items.FirstOrDefault(x => picker.Chosen[0].Owns(x.CliId)) is { } added) Conversations.SelectedItem = added;
        }, L.AddFailed);
    }

    private static bool SamePath(string a, string b)
        => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsLinked: false } item || demo) return;
        var known = config.Projects.FirstOrDefault(x => x.Id == item.ProjectId)?.Path;
        string? folder = known is not null && Directory.Exists(known) ? known : null;
        if (folder is not null)
        {
            var answer = Dialog.Show(this, L.JoinTitle, L.JoinText(item.Title, folder), Tone.Attention, L.TakeToThisComputer, L.OtherFolder, L.Cancel);
            if (answer == 1) folder = null;
            else if (answer != 0) return;
        }
        folder ??= PickFolder(L.PickConversationFolder(item.Title), GuessFolder(item.ProjectName));
        if (folder is null) return;

        await Guarded(async () =>
        {
            config.Projects.RemoveAll(x => x.Id == item.ProjectId);
            config.Projects.Add(new ProjectMap { Id = item.ProjectId, Name = item.ProjectName, Path = folder });
            await cloud.Link(config.DeviceId, item.Id);
            if (!config.Subscriptions.Any(x => x.SessionId == item.Id))
                config.Subscriptions.Add(new Subscription { SessionId = item.Id, ProjectId = item.ProjectId, CliSessionId = item.CliId });
            Save();
            await SyncCore(async () => [await engine.SyncOne(item.Id)]);
        }, L.JoinFailed);
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item || demo) return;
        var folder = PickFolder(L.PickProjectFolder(item.ProjectName), item.LocalPath ?? GuessFolder(item.ProjectName));
        if (folder is null) return;
        await Guarded(async () =>
        {
            config.Projects.RemoveAll(x => x.Id == item.ProjectId);
            config.Projects.Add(new ProjectMap { Id = item.ProjectId, Name = item.ProjectName, Path = folder });
            Save();
            await SyncCore();
        }, L.FolderSaveFailed);
    }

    private async void SyncOne_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsLinked: true } item || demo) return;
        await Guarded(() => SyncCore(async () => [await engine.SyncOne(item.Id)]), L.ErrorTitle);
    }

    private async void KeepLocal_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsConflict: true } item) return;
        if (!Dialog.Ask(this, L.KeepLocalTitle, L.KeepLocalText, L.KeepThisComputer)) return;
        await Guarded(() => SyncCore(async () => [await engine.SyncOne(item.Id, Resolution.KeepLocal)]), L.ConflictFailed);
    }

    private async void TakeCloud_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsConflict: true } item) return;
        if (!Dialog.Ask(this, L.TakeCloudTitle, L.TakeCloudText + (snapshot.DesktopRunning ? L.TakeCloudRestart : ""), L.TakeCloud)) return;
        await WithClaudeClosed(async () => [await engine.SyncOne(item.Id, Resolution.TakeCloud)]);
    }

    private async void CloseClaude_Click(object sender, RoutedEventArgs e) => await CloseClaudeAndApply();

    private async Task CloseClaudeAndApply()
    {
        if (!SignedIn || demo) return;
        ShowWindow();
        if (!Dialog.Ask(this, L.CloseClaudeTitle, L.CloseClaudeText, L.CloseAndApply)) return;
        await WithClaudeClosed(() => engine.SyncAll());
    }

    /// <summary>Quits Claude Desktop, runs a sync step, then starts Claude Desktop again.</summary>
    private async Task WithClaudeClosed(Func<Task<IReadOnlyList<SessionReport>>> work)
    {
        await Guarded(() => WhileClaudeClosed(async () => { await SyncCore(work); return true; }), L.CloseClaudeFailed);
        await Watch();
    }

    /// <summary>Caller holds <see cref="gate"/>. Exceptions from <paramref name="work"/> propagate.</summary>
    private async Task<T> WhileClaudeClosed<T>(Func<Task<T>> work)
    {
        string? restart = null;
        try
        {
            if (ClaudeProcesses.Snapshot().DesktopRunning)
            {
                FooterStatus.Text = L.ClosingDesktop;
                restart = await ClaudeProcesses.CloseDesktop(allowForce: () => true);
            }
            snapshot = ClaudeProcesses.Snapshot();
            return await work();
        }
        finally
        {
            try { ClaudeProcesses.Reopen(restart); }
            catch (Exception ex)
            {
                Store.Log("Reopen Claude: " + ex);
                Dialog.Info(this, L.ReopenFailedTitle, L.ReopenFailedText + ex.Message, Tone.Warning);
            }
        }
    }

    private void Backups_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item || demo) return;
        Func<BackupItem, Task<SessionReport>>? restore = item.IsLinked ? async backup =>
        {
            await gate.WaitAsync();
            try { return await WhileClaudeClosed(() => Task.Run(() => engine.Restore(backup))); }
            finally { gate.Release(); _ = SyncNow(); }
        } : null;
        new BackupsWindow(engine, item, restore) { Owner = this }.ShowDialog();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.LocalPath is { } path && Directory.Exists(path)) OpenInExplorer(path);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item || demo) return;
        if (!Dialog.Ask(this, L.RemoveTitle, L.RemoveText(item.Title), L.RemoveFromSync, Tone.Warning)) return;
        await Guarded(async () =>
        {
            await cloud.DeleteSession(item.Id);
            Forget(item.Id);
            await SyncCore();
        }, L.RemoveFailed);
    }

    /// <summary>Stops syncing a conversation on this computer. Claude's own files are left alone. Caller holds <see cref="gate"/>.</summary>
    private void Forget(string sessionId)
    {
        config.Subscriptions.RemoveAll(x => x.SessionId == sessionId);
        Save();
        var state = engine.LoadState();
        state.Base.Remove(sessionId);
        state.Reports.Remove(sessionId);
        Store.Save(Path.Combine(Store.Home, "state.json"), state);
        var cache = Path.Combine(Store.Home, "cache");
        if (Directory.Exists(cache))
            foreach (var file in Directory.GetFiles(cache, sessionId + "-*.zip"))
                try { File.Delete(file); } catch (IOException) { }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e) => await SignIn();

    private async Task SignIn()
    {
        SignInButton.IsEnabled = false;
        SignInText.Text = L.WaitingForBrowser;
        try
        {
            await cloud.SignIn();
            var (userId, email) = await cloud.User();
            await Guarded(async () =>
            {
                if (config.UserId.Length > 0 && config.UserId != userId)
                {
                    // Different Google account: its conversations and devices are unrelated to the old ones.
                    config.Projects.Clear(); config.Subscriptions.Clear(); config.DeviceId = "";
                    Store.Save(Path.Combine(Store.Home, "state.json"), new SyncState());
                    items.Clear();
                }
                config.UserId = userId;
                config.Email = email;
                var current = await cloud.Library();
                if (!(current["devices"]?.AsArray() ?? []).Any(x => P(x, "id") == config.DeviceId))
                    config.DeviceId = await cloud.RegisterDevice(Environment.MachineName);
                Save();
            }, L.SignInFailedTitle);
            UpdateAccount();
            await SyncNow();
        }
        catch (Exception ex) { Dialog.Info(this, L.SignInFailedTitle, ex.Message, Tone.Error); }
        finally
        {
            SignInButton.IsEnabled = true;
            SignInText.Text = L.SignInWithGoogle;
        }
    }

    private void Account_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AccountButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = config.Email ?? L.GoogleAccount, IsEnabled = false });
        menu.Items.Add(new MenuItem { Header = L.ThisComputer(Environment.MachineName), IsEnabled = false });
        menu.Items.Add(new Separator());
        var change = new MenuItem { Header = L.SwitchAccount };
        change.Click += async (_, _) => await SignIn();
        var signOut = new MenuItem { Header = L.SignOut };
        signOut.Click += (_, _) =>
        {
            if (!Dialog.Ask(this, L.SignOutTitle, L.SignOutText, L.SignOut, Tone.Warning)) return;
            cloud.SignOut();
            UpdateAccount();
        };
        menu.Items.Add(change);
        menu.Items.Add(signOut);
        menu.IsOpen = true;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (demo) return;
        var window = new SettingsWindow(config, claude) { Owner = this };
        if (window.ShowDialog() != true) return;
        config = Store.Load<AppConfig>(Store.ConfigPath);
        CreateServices();
        _ = SyncNow();
    }

    private void Support_Click(object sender, RoutedEventArgs e) => Links.Open(Links.Donate);
    private void SetupGuide_Click(object sender, RoutedEventArgs e) => Links.Open(Links.SetupGuide);

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var values = CodeBox.Text.Trim().Length > 0
            ? AppConfig.ParseConnectionCode(CodeBox.Text)
            : AppConfig.IsHttps(SupabaseBox.Text) && AppConfig.IsHttps(WorkerBox.Text) && KeyBox.Text.Trim().Length > 0
                ? (SupabaseBox.Text.Trim(), KeyBox.Text.Trim(), WorkerBox.Text.Trim()) : null;
        if (values is not { } server)
        {
            Dialog.Info(this, L.InvalidServerTitle, CodeBox.Text.Trim().Length > 0 ? L.InvalidCode : L.InvalidServerText, Tone.Warning);
            return;
        }
        config.SupabaseUrl = server.Supabase.TrimEnd('/');
        config.PublishableKey = server.Key;
        config.WorkerUrl = server.Worker.TrimEnd('/');
        Save();
        CreateServices();
        UpdateAccount();
        UpdateFooter();
    }

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Store.Home);
        OpenInExplorer(File.Exists(Store.LogPath) ? Store.LogPath : Store.Home);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static void OpenInExplorer(string path)
    {
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        if (File.Exists(path)) info.ArgumentList.Add("/select," + path); else info.ArgumentList.Add(path);
        Process.Start(info);
    }

    private string? PickFolder(string title, string? initial)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (initial is not null && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    /// <summary>Projects usually live side by side: suggest &lt;parent of a known project&gt;\&lt;name&gt;.</summary>
    private string? GuessFolder(string projectName)
    {
        var parents = config.Projects.Select(x => Path.GetDirectoryName(x.Path)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return parents.Select(p => Path.Combine(p, projectName)).FirstOrDefault(Directory.Exists) ?? parents.FirstOrDefault(Directory.Exists);
    }

    // ---- design preview (--demo) -------------------------------------------------------------------------

    private int? previewIndex;
    public void SelectForPreview(int index)
    {
        previewIndex = index;
        if (index < view.Count) Conversations.SelectedItem = view.GetItemAt(index);
    }

    private void LoadDemo()
    {
        config.Email = "you@example.com";
        DeviceName.Text = "DESKTOP-PC";
        UpdateAccount();
        snapshot = new ClaudeSnapshot([new ClaudeProcess(1, ClaudeProcessKind.Desktop, "", null)]);
        var samples = new (string Title, string Project, bool Linked, SessionReport? Report)[]
        {
            (L.Turkish ? "Ürün planını oku" : "Read the product plan", "movee", true, new SessionReport { Status = SyncStatus.PendingDownload, RemoteOnly = 42, Message = L.BlockedDesktop }),
            (L.Turkish ? "Uçuş simülasyonu fizik motoru" : "Flight simulation physics", "embodied-fly", true, new SessionReport { Status = SyncStatus.UpToDate }),
            (L.Turkish ? "Supabase edge function testleri" : "Supabase edge function tests", "movee", true, new SessionReport { Status = SyncStatus.Conflict, LocalOnly = 6, RemoteOnly = 11 }),
            (L.Turkish ? "Landing page yeniden tasarım" : "Landing page redesign", "digitalKarma", true, new SessionReport { Status = SyncStatus.Uploaded }),
            (L.Turkish ? "Karma puanı algoritması" : "Karma score algorithm", "digitalKarma", false, null),
            (L.Turkish ? "Onboarding akışı" : "Onboarding flow", "movee", false, null),
        };
        Show(samples.Select(sample =>
        {
            var item = new ConversationItem(Guid.NewGuid().ToString()) { Title = sample.Title, ProjectName = sample.Project };
            item.LocalPath = sample.Linked ? System.IO.Path.Combine(@"C:\Users\you\Projects", sample.Project) : null;
            item.Apply(sample.Linked, sample.Report, sample.Linked ? new SyncBase { SyncedAt = DateTimeOffset.Now.AddMinutes(-7) } : null);
            return item;
        }).ToArray());
        lastCheck = DateTimeOffset.Now.AddSeconds(-20);
        Render();
    }
}
