using System.Windows;
using Purrch.Core;
using Forms = System.Windows.Forms;

namespace Purrch.Interface;

/// Notification-area icon with Open, Settings and Quit. Left-click opens the panel.
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Purrch", null, (_, _) => NotchState.Shared.Present());
        menu.Items.Add("Settings", null, (_, _) => NotchState.Shared.Present(settings: true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Purrch", null, (_, _) => Application.Current.Shutdown());

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Purrch",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) NotchState.Shared.Present();
        };
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Tray.ico"));
        using var stream = resource.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        // Without this the icon lingers in the tray until the pointer passes over it.
        _icon.Visible = false;
        _icon.Dispose();
    }
}
