//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace RemoteGameHub.App;

// Autostart at sign-in, switched from the tray menu. A scheduled task and not the Run key, which
// silently skips a program whose manifest asks for administrator rights.
internal sealed class Autostart
{
    private readonly string _directory;
    private volatile bool _enabled;

    internal Autostart(string directory) => _directory = directory;

    // Whether the task exists, as last read. Read from memory because the menu asks on the
    // thread it is drawn on, where a process cannot be waited for.
    internal bool IsEnabled => _enabled;

    // Whether the task that exists starts this copy. A task left by a copy that has moved — from
    // a download folder to Program Files, most often — starts the old one at every sign-in.
    internal bool StartsThisCopy => _startsThisCopy;

    private volatile bool _startsThisCopy;

    // Asks Task Scheduler whether the task is there and what it starts, and remembers both.
    internal void Refresh()
    {
        var (code, output) = Run("/Query", "/TN", AppParameters.Identity.StartupTask, "/XML");

        _enabled = code == 0;
        _startsThisCopy = _enabled && StartsHere(output);
    }

    // Creates the task if it is absent, replaces it if it points somewhere else, and removes it
    // if it is this copy's. The log is the only place a switch with no window can report to.
    internal void Toggle()
    {
        Refresh();

        try
        {
            if (!_enabled) Create();
            else if (!_startsThisCopy) Create(replacing: true);
            else Remove();
        }
        catch (Exception error)
        {
            Log.Warn($"starting with Windows could not be switched: {error.Message}");
        }

        Refresh();
    }

    // The command inside the task's own definition, against this executable. Anything that cannot
    // be read counts as a match: churning a task over an answer nobody understood is worse.
    private static bool StartsHere(string definition)
    {
        var here = Environment.ProcessPath;
        if (string.IsNullOrEmpty(here)) return true;

        var command = System.Text.RegularExpressions.Regex.Match(
            definition, @"<Command>\s*(.*?)\s*</Command>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Singleline);

        if (!command.Success) return true;

        var written = command.Groups[1].Value.Trim().Trim('"');
        return string.Equals(written, here, StringComparison.OrdinalIgnoreCase);
    }

    // /F overwrites, so replacing a task that points elsewhere is the same call as making one.
    private void Create(bool replacing = false)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
            throw new InvalidOperationException("the path of this executable is not known");

        // Written beside the configuration, handed to schtasks, and deleted. schtasks reads
        // the file it is given and keeps its own copy; nothing of this needs to outlive the call.
        var definition = Path.Combine(_directory, AppParameters.Identity.FileBase + "-task.xml");
        File.WriteAllText(definition, Definition(executable), new UnicodeEncoding(false, true));

        try
        {
            var (code, output) = Run("/Create", "/TN", AppParameters.Identity.StartupTask,
                                     "/XML", definition, "/F");
            if (code != 0)
                throw new InvalidOperationException($"schtasks answered {code}: {output}");
        }
        finally
        {
            try
            {
                File.Delete(definition);
            }
            catch (Exception)
            {
                // A definition left behind is a small file in the server's own folder.
            }
        }

        Log.Event((replacing
                      ? $"the task \"{AppParameters.Identity.StartupTask}\" started another copy " +
                        "and has been rewritten: "
                      : "the server will start at sign-in: ") +
                  $"it runs {executable} with administrator rights");
    }

    private static void Remove()
    {
        var (code, output) = Run("/Delete", "/TN", AppParameters.Identity.StartupTask, "/F");
        if (code != 0)
            throw new InvalidOperationException($"schtasks answered {code}: {output}");

        Log.Event("the server will no longer start at sign-in; the scheduled task was removed");
    }

    // XML through schtasks.exe rather than its switches, which cannot say two things that matter:
    // no stop after seventy-two hours, and normal rather than below-normal priority.
    private static string Definition(string executable)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        // The person signed in, not this process: the writer is usually the service's worker,
        // LocalSystem, and a task triggered by SYSTEM signing in would wait for ever.
        string user;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            user = UserContext.ConsoleUserName() ?? identity.Name;
        }

        var task = new XElement(ns + "Task", new XAttribute("version", "1.4"),
            new XElement(ns + "RegistrationInfo",
                new XElement(ns + "Description",
                    $"Starts {AppParameters.Identity.DisplayName} at sign-in, with administrator " +
                    "rights. Switched from the tray menu.")),
            new XElement(ns + "Triggers",
                new XElement(ns + "LogonTrigger",
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "UserId", user),
                    // A delay after sign-in: the graphics driver and the audio service are still
                    // coming up, and a server that starts into them finds no screen and refuses.
                    new XElement(ns + "Delay", "PT10S"))),
            new XElement(ns + "Principals",
                new XElement(ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(ns + "UserId", user),
                    new XElement(ns + "LogonType", "InteractiveToken"),
                    new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings",
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(ns + "StopIfGoingOnBatteries", "false"),
                new XElement(ns + "AllowHardTerminate", "false"),
                new XElement(ns + "StartWhenAvailable", "false"),
                new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(ns + "IdleSettings",
                    new XElement(ns + "StopOnIdleEnd", "false"),
                    new XElement(ns + "RestartOnIdle", "false")),
                new XElement(ns + "AllowStartOnDemand", "true"),
                new XElement(ns + "Enabled", "true"),
                new XElement(ns + "Hidden", "false"),
                new XElement(ns + "RunOnlyIfIdle", "false"),
                new XElement(ns + "WakeToRun", "false"),
                // PT0S is "no limit". The default is three days, after which the task — and
                // with it the server — is stopped.
                new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                // 4 is the normal priority class. Tasks default to 7, below normal, which is
                // the wrong place for something that has to keep up with a screen.
                new XElement(ns + "Priority", "4")),
            new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(ns + "Exec",
                    new XElement(ns + "Command", $"\"{executable}\""),
                    new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(executable)))));

        // The declaration is written by hand: XDocument.ToString drops it, and schtasks reads
        // the encoding from it.
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + Environment.NewLine + task;
    }

    // Runs schtasks with no window and returns its exit code and whatever it said. The
    // text is in the user's language and is only ever written to the log.
    private static (int Code, string Output) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("schtasks.exe did not start");

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, output.Trim());
    }
}
