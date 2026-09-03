//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Windows.Forms;

namespace RemoteGameHub.App;

// How a refusal reaches the user when there is no window to put it in. A balloon, briefly, and
// then the process ends.
internal static class StartupNotice
{
    // Long enough to read, short enough not to leave a dead icon in the tray.
    private const int VisibleMs = 12_000;

    // Shows the first line of the reason and points at the log for the rest. Never a dialog: the
    // person who would answer it is not at this machine.
    internal static void Show(string reason)
    {
        var headline = reason.Replace("\r\n", "\n").Split('\n')[0].Trim();

        try
        {
            using var tray = new TrayIcon();
            tray.SetState("not started");
            tray.Notify(
                $"{AppParameters.Identity.DisplayName} did not start",
                Log.Path is null
                    ? headline
                    : $"{headline}\nThe full reason is in {Log.Path}.",
                isError: true);

            // The balloon is drawn by the shell on this thread's message queue, so the queue has
            // to keep running for it to appear at all. A Sleep here shows nothing.
            var timer = new System.Windows.Forms.Timer { Interval = VisibleMs };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Application.ExitThread();
            };
            timer.Start();

            Application.Run();
            timer.Dispose();
        }
        catch (Exception error)
        {
            // The notice failing is not worth a second failure on top of the first.
            Log.Warn($"could not show the startup notice: {error.Message}");
        }
    }
}
