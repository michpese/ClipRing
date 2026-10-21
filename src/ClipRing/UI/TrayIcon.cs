using System.Drawing;
using System.Windows.Forms;
using ClipRing.Core;

namespace ClipRing.UI;

/// <summary>Notification-area icon: ClipRing keeps running there while the window is closed.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(Action open, Action saveClip, Action togglePause, Action openFolder, Action exit, Func<bool> isPaused)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Öffnen", null, (_, _) => open());
        menu.Items.Add("Clip speichern", null, (_, _) => saveClip());
        var pause = new ToolStripMenuItem("Replay pausieren", null, (_, _) => togglePause());
        menu.Items.Add(pause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Clip-Ordner öffnen", null, (_, _) => openFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => exit());
        menu.Opening += (_, _) => pause.Text = isPaused() ? "Replay fortsetzen" : "Replay pausieren";

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "ClipRing",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) open();
        };
    }

    private static Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null && Icon.ExtractAssociatedIcon(exe) is { } icon) return icon;
        }
        catch (Exception ex)
        {
            Log.Warn("Programmsymbol nicht ladbar: " + ex.Message);
        }
        return SystemIcons.Application;
    }

    public void ShowBalloon(string title, string text)
    {
        try { _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.None); }
        catch { /* notifications disabled */ }
    }

    public void SetText(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
