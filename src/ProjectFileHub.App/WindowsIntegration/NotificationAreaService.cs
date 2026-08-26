using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace ProjectFileHub.App.WindowsIntegration;

internal sealed record NotificationAreaSnapshot(
    string ActiveProjectName,
    string IndexStatus,
    bool CanToggleIndex,
    bool IsIndexPaused);

internal sealed class NotificationAreaService : IDisposable
{
    private readonly Icon _icon;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _activeProjectItem;
    private readonly ToolStripMenuItem _indexStatusItem;
    private readonly ToolStripMenuItem _toggleIndexItem;
    private readonly Func<NotificationAreaSnapshot> _getSnapshot;
    private bool _backgroundTipShown;
    private bool _disposed;

    public NotificationAreaService(
        string iconPath,
        Action showWindow,
        Action toggleIndex,
        Action exitApplication,
        Func<NotificationAreaSnapshot> getSnapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iconPath);
        ArgumentNullException.ThrowIfNull(showWindow);
        ArgumentNullException.ThrowIfNull(toggleIndex);
        ArgumentNullException.ThrowIfNull(exitApplication);
        ArgumentNullException.ThrowIfNull(getSnapshot);

        _getSnapshot = getSnapshot;
        _icon = new Icon(iconPath);
        _activeProjectItem = new ToolStripMenuItem { Enabled = false };
        _indexStatusItem = new ToolStripMenuItem { Enabled = false };
        _toggleIndexItem = new ToolStripMenuItem();
        _toggleIndexItem.Click += (_, _) => toggleIndex();

        var openItem = new ToolStripMenuItem("打开 Project File Hub");
        openItem.Click += (_, _) => showWindow();
        var exitItem = new ToolStripMenuItem("完全退出");
        exitItem.Click += (_, _) => exitApplication();

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add(openItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(_activeProjectItem);
        _contextMenu.Items.Add(_indexStatusItem);
        _contextMenu.Items.Add(_toggleIndexItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(exitItem);
        _contextMenu.Opening += OnContextMenuOpening;

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _contextMenu,
            Icon = _icon,
            Text = "Project File Hub",
            Visible = false
        };
        _notifyIcon.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                showWindow();
            }
        };
    }

    public bool IsVisible
    {
        get => _notifyIcon.Visible;
        set
        {
            ThrowIfDisposed();
            _notifyIcon.Visible = value;
        }
    }

    public void ShowBackgroundTip()
    {
        ThrowIfDisposed();
        if (_backgroundTipShown || !_notifyIcon.Visible)
        {
            return;
        }

        _backgroundTipShown = true;
        _notifyIcon.ShowBalloonTip(
            2500,
            "Project File Hub",
            "仍在后台运行。双击右下角图标可重新打开。",
            ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _icon.Dispose();
    }

    private void OnContextMenuOpening(object? sender, CancelEventArgs e)
    {
        var snapshot = _getSnapshot();
        _activeProjectItem.Text = $"当前项目：{EscapeMenuText(snapshot.ActiveProjectName)}";
        _indexStatusItem.Text = EscapeMenuText(snapshot.IndexStatus);
        _toggleIndexItem.Enabled = snapshot.CanToggleIndex;
        _toggleIndexItem.Text = snapshot.IsIndexPaused ? "恢复后台索引" : "暂停后台索引";

        var tooltip = string.IsNullOrWhiteSpace(snapshot.ActiveProjectName)
            ? "Project File Hub"
            : $"Project File Hub · {snapshot.ActiveProjectName}";
        _notifyIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
    }

    private static string EscapeMenuText(string value) => value.Replace("&", "&&", StringComparison.Ordinal);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
