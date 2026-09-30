using System.Windows;
using System.Windows.Controls;

namespace ClaudeSync;

public partial class BackupsWindow : Window
{
    public sealed record Row(BackupItem Item)
    {
        public string When => Item.Created == DateTimeOffset.MinValue ? L.UnknownDate : Text.Date(Item.Created);
        public string Label => Item.Label;
        public bool Current => Item.Current;
        public string Glyph => Item.Source switch { "cloud" => "\uE753", "zip" => "\uE7BA", _ => "\uE81C" };
    }

    private readonly SyncEngine engine;
    private readonly ConversationItem conversation;
    private readonly Func<BackupItem, Task<SessionReport>>? restore;

    public BackupsWindow(SyncEngine engine, ConversationItem conversation, Func<BackupItem, Task<SessionReport>>? restore)
    {
        InitializeComponent();
        this.engine = engine; this.conversation = conversation; this.restore = restore;
        Subtitle.Text = conversation.Title;
        Footer.Text = restore is null ? L.RestoreNeedsJoin : "";
        Loaded += async (_, _) => await Reload();
    }

    private async Task Reload()
    {
        List.ItemsSource = null;
        Status.Text = L.LoadingBackups;
        Status.Visibility = Visibility.Visible;
        try
        {
            var backups = await Task.Run(() => engine.Backups(conversation.Id, conversation.CliId));
            List.ItemsSource = backups.Select(x => new Row(x)).ToArray();
            Status.Text = L.NoBackups;
            Status.Visibility = backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Store.Log("Backups: " + ex);
            Status.Text = L.BackupsFailed + ex.Message;
        }
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RestoreButton.IsEnabled = restore is not null && List.SelectedItem is Row { Current: false };

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (restore is null || List.SelectedItem is not Row row) return;
        if (!Dialog.Ask(this, L.RestoreTitle, L.RestoreText(conversation.Title, row.When), L.Restore)) return;
        RestoreButton.IsEnabled = false;
        Footer.Text = L.Restoring;
        try
        {
            var report = await restore(row.Item);
            Footer.Text = report.Status is SyncStatus.Uploaded or SyncStatus.UpToDate
                ? L.Restored
                : L.RestoredLocally + report.Message;
            await Reload();
        }
        catch (Exception ex)
        {
            Footer.Text = "";
            Dialog.Info(this, L.RestoreFailed, ex.Message, Tone.Error);
        }
    }
}
