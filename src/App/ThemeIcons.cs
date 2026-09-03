//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RemoteGameHub.App;

// The application's icon, and the one theme switch anything here follows. There were four tray
// drawings chosen by the notification area's colour; there is one now, in colour, on every theme.
internal static class ThemeIcons
{
    private const string PersonalizeKey =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // Whether applications are drawn dark — the switch the menu, the page and the starting card
    // follow. A high-contrast theme answers by its window colour instead.
    internal static bool AppsAreDark()
    {
        if (SystemInformation.HighContrast)
        {
            var background = SystemColors.Window;
            var brightness = (background.R * 299 + background.G * 587 + background.B * 114) / 1000;
            return brightness <= 128;
        }

        var value = Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", null);
        return value is int light && light == 0;
    }

    // Loads the drawing at the size the shell actually asks for: handed a 32-pixel icon for a
    // 16-pixel slot, the shell scales it without smoothing and thin strokes come apart.
    internal static Icon Load()
    {
        const string name = "RemoteGameHub.Icons.RemoteGameHub.ico";

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"The embedded icon \"{name}\" is missing from the executable.");

        return new Icon(stream, SystemInformation.SmallIconSize);
    }
}
