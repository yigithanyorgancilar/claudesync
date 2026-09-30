using Forms = System.Windows.Forms;

namespace ClaudeSync;

/// <summary>Notification-area icon: keeps ClaudeSync reachable while the window is closed.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon;

    public TrayIcon(Action open, Action sync, Action closeClaudeAndSync, Action exit)
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ClaudeSync.ico")).Stream;
        icon = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize), Text = "ClaudeSync", Visible = true };
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        var openItem = menu.Items.Add(L.TrayOpen, null, (_, _) => open());
        openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(L.SyncNow, null, (_, _) => sync());
        menu.Items.Add(L.TrayCloseAndSync, null, (_, _) => closeClaudeAndSync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.TrayExit, null, (_, _) => exit());
        icon.ContextMenuStrip = menu;
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
        icon.BalloonTipClicked += (_, _) => open();
    }

    public void Notify(string title, string text, Forms.ToolTipIcon kind = Forms.ToolTipIcon.Info)
        => icon.ShowBalloonTip(6000, title, text.Length > 250 ? text[..250] + "…" : text, kind);

    /// <summary>Tooltip text (Windows limits it to 63 characters).</summary>
    public void SetStatus(string text) => icon.Text = text.Length > 63 ? text[..62] + "…" : text;

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
