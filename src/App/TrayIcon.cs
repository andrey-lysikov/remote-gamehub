//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RemoteGameHub.App;

// One entry of the tray menu; a null action is a separator. IsChecked is asked each time the menu
// opens and must answer from memory: it runs on the thread the menu is being drawn on.
internal sealed record TrayEntry(string Label, Action? Do, Func<bool>? IsChecked = null)
{
    internal static readonly TrayEntry Separator = new(string.Empty, null);
}

// The whole user interface: one icon in the notification area and a short menu behind the right
// button. Every entry does its work in silence or hands a file to whatever Windows opens it with.
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    private Icon? _drawing;
    private ContextMenuStrip? _menu;
    private string _state = "starting";
    private bool _disposed;

    internal TrayIcon()
    {
        _drawing = ThemeIcons.Load();

        // No Text, and deliberately: the icon has no tooltip. The shell allows sixty-three
        // characters for one; what the server is doing is on the page and in the log instead.
        _icon = new NotifyIcon
        {
            Icon = _drawing,
            Visible = true,
        };

        _icon.BalloonTipClicked += OnBalloonClicked;
        _icon.BalloonTipClosed += OnBalloonClosed;

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Log.Info($"tray icon shown, {SystemInformation.SmallIconSize.Width} px");
    }

    // What the server is doing, in one line, written to the log rather than shown as a tooltip.
    // Only changes are written: this is called whenever something might have moved.
    internal void SetState(string state)
    {
        if (_disposed || state == _state) return;

        _state = state;
        Log.Event($"state: {state}");
    }

    // Puts a menu behind the right button; an entry whose action is null is a separator. Each
    // action runs on the message loop's thread, so anything slower must hand its work to another.
    internal void SetMenu(IEnumerable<TrayEntry> entries)
    {
        var list = entries.ToList();

        // The check margin is shown only when something can be checked: on a menu of plain
        // commands it is an empty column that makes every entry look indented for no reason.
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = list.Any(entry => entry.IsChecked is not null),
            Renderer = MenuRendererFor(ThemeIcons.AppsAreDark()),
        };

        foreach (var entry in list)
        {
            if (entry.Do is null)
            {
                menu.Items.Add(new ToolStripSeparator());
                continue;
            }

            var action = entry.Do;
            var item = new ToolStripMenuItem(entry.Label);
            item.Click += (_, _) =>
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    // A menu entry that throws must not take the message loop with it: this is
                    // the thread the tray icon itself lives on.
                    Log.Info($"the menu entry \"{entry.Label}\" failed: {error.GetType().Name}: {error.Message}");
                }
            };

            if (entry.IsChecked is { } isChecked)
            {
                // Asked as the menu opens rather than once: the switch can be moved from outside
                // this process — the task deleted in Task Scheduler, say.
                menu.Opening += (_, _) => item.Checked = isChecked();
            }

            menu.Items.Add(item);
        }

        _icon.ContextMenuStrip = menu;
        _menu = menu;
    }

    // The menu's colours for the theme in force. Under a high-contrast theme the system renderer
    // is kept: those themes exist to be obeyed exactly.
    private static ToolStripRenderer MenuRendererFor(bool dark) =>
        SystemInformation.HighContrast ? new ToolStripSystemRenderer() : new MenuRenderer(dark);

    // A balloon, and a toast in disguise: with notifications off it never appears at all, so
    // nothing that only exists as a balloon may be load-bearing. onClick runs when it is clicked.
    internal void Notify(string title, string message, bool isError, Action? onClick = null)
    {
        if (_disposed) return;

        _onBalloonClick = onClick;

        _icon.BalloonTipIcon = isError ? ToolTipIcon.Error : ToolTipIcon.Info;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(10_000);
    }

    // What the balloon on screen does when clicked. One at a time: a new balloon replaces the
    // old one on screen, and its action replaces the old action here.
    private Action? _onBalloonClick;

    private void OnBalloonClicked(object? sender, EventArgs e)
    {
        var action = _onBalloonClick;
        _onBalloonClick = null;

        try
        {
            action?.Invoke();
        }
        catch (Exception error)
        {
            Log.Info($"the balloon's action failed: {error.GetType().Name}: {error.Message}");
        }
    }

    private void OnBalloonClosed(object? sender, EventArgs e) => _onBalloonClick = null;

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // The theme arrives as General or as Color depending on which switch moved, and high
        // contrast as Accessibility, so it is re-read and the icon replaced only when it changed.
        if (e.Category is not (UserPreferenceCategory.General
                            or UserPreferenceCategory.Color
                            or UserPreferenceCategory.Accessibility
                            or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        if (_disposed) return;

        // Only the menu: the icon is one coloured drawing on every theme and never changes.
        if (_menu is null) return;

        try
        {
            _menu.Renderer = MenuRendererFor(ThemeIcons.AppsAreDark());
        }
        catch (Exception error)
        {
            Log.Info($"the menu could not be recoloured for the theme: {error.Message}");
        }
    }


    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        // Hidden before it is disposed: a NotifyIcon that is only disposed leaves its slot behind
        // until something makes the shell repaint the notification area.
        _icon.Visible = false;
        _icon.Dispose();
        _menu?.Dispose();
        _drawing?.Dispose();

        Log.Event($"tray icon removed (last state: {_state})");
    }
}
