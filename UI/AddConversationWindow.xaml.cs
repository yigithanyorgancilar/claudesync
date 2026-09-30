using System.Windows;
using System.Windows.Controls;

namespace ClaudeSync;

public partial class AddConversationWindow : Window
{
    public sealed record Row(LocalSession Session)
    {
        public string Title => Session.Title;
        public string Subtitle => $"{Session.ProjectName}  ·  {Text.Relative(Session.LastActivity)}" + (Session.Archived ? "  ·  " + L.Archived : "");
        public string Folder => Session.Cwd;
    }

    private readonly Row[] rows;
    public IReadOnlyList<LocalSession> Chosen { get; private set; } = [];

    public AddConversationWindow(IReadOnlyList<LocalSession> sessions)
    {
        InitializeComponent();
        rows = sessions.Select(x => new Row(x)).ToArray();
        List.ItemsSource = rows;
        Empty.Text = L.NothingToAdd;
        Empty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
        Loaded += (_, _) => Search.Focus();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var selected = List.SelectedItems.Cast<Row>().ToHashSet();
        var query = Search.Text.Trim();
        List.ItemsSource = query.Length == 0 ? rows : rows.Where(r => r.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || r.Folder.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        foreach (var row in List.Items.Cast<Row>().Where(selected.Contains)) List.SelectedItems.Add(row);
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCount();

    private void UpdateCount()
    {
        var count = List.SelectedItems.Count;
        Count.Text = count == 0 ? L.ConversationCount(rows.Length) : L.SelectedCount(count);
        AddButton.IsEnabled = count > 0;
        AddButton.Content = count > 1 ? L.AddCount(count) : L.Add;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Chosen = List.SelectedItems.Cast<Row>().Select(x => x.Session).ToArray();
        DialogResult = true;
    }
}
