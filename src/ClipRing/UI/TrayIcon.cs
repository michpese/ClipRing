using System.Drawing;
using System.Windows.Forms;
using ClipRing.Core;
using ClipRing.Localization;

namespace ClipRing.UI;

/// <summary>Notification-area icon: ClipRing keeps running there while the window is closed.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _open, _save, _pause, _folder, _exit;
    private readonly Func<bool> _isPaused;

    public TrayIcon(Action open, Action saveClip, Action togglePause, Action openFolder, Action exit, Func<bool> isPaused)
    {
        _isPaused = isPaused;
        var menu = new ContextMenuStrip();
        _open = new ToolStripMenuItem("", null, (_, _) => open());
        _save = new ToolStripMenuItem("", null, (_, _) => saveClip());
        _pause = new ToolStripMenuItem("", null, (_, _) => togglePause());
        _folder = new ToolStripMenuItem("", null, (_, _) => openFolder());
        _exit = new ToolStripMenuItem("", null, (_, _) => exit());
        menu.Items.Add(_open);
        menu.Items.Add(_save);
        menu.Items.Add(_pause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_folder);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exit);
        menu.Opening += (_, _) => ApplyTexts();
        ApplyTexts();

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

    /// <summary>Menu texts in the current UI language.</summary>
    public void ApplyTexts()
    {
        _open.Text = Loc.T("Btn_Open");
        _save.Text = Loc.T("Btn_SaveClip");
        _pause.Text = Loc.T(_isPaused() ? "Btn_ResumeReplay" : "Btn_PauseReplay");
        _folder.Text = Loc.T("Btn_OpenClipFolder");
        _exit.Text = Loc.T("Btn_Exit");
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
