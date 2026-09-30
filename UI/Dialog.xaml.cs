using System.Windows;
using System.Windows.Controls;

namespace ClaudeSync;

/// <summary>Themed replacement for MessageBox.</summary>
public partial class Dialog : Window
{
    private int choice = -1;
    private sealed record ToneHolder(Tone Tone);

    internal Dialog(Window? owner, string title, string message, Tone tone, string[] buttons)
    {
        InitializeComponent();
        if (owner is { IsVisible: true }) Owner = owner; else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Heading.Text = title;
        Body.Text = message;
        ToneIcon.DataContext = new ToneHolder(tone);
        ToneIcon.Text = tone switch { Tone.Success => "\uE930", Tone.Warning => "\uE7BA", Tone.Error => "\uEA39", Tone.Attention => "\uE946", _ => "\uE946" };
        // buttons[0] is the primary action; it goes last (right-most), Windows style.
        for (var i = buttons.Length - 1; i >= 0; i--)
        {
            var index = i;
            var button = new Button { Content = buttons[i], MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = i == 0, IsCancel = i == buttons.Length - 1 && buttons.Length > 1 };
            if (i == 0) button.Style = (Style)FindResource("PrimaryButton");
            button.Click += (_, _) => { choice = index; Close(); };
            Buttons.Children.Add(button);
        }
    }

    /// <returns>Index of the clicked button (0 = primary), or -1 if the window was closed.</returns>
    public static int Show(Window? owner, string title, string message, Tone tone, params string[] buttons)
    {
        var dialog = new Dialog(owner, title, message, tone, buttons);
        dialog.ShowDialog();
        return dialog.choice;
    }

    public static bool Ask(Window? owner, string title, string message, string confirm, Tone tone = Tone.Attention)
        => Show(owner, title, message, tone, confirm, L.Cancel) == 0;

    public static void Info(Window? owner, string title, string message, Tone tone = Tone.Attention)
        => Show(owner, title, message, tone, L.Ok);
}
