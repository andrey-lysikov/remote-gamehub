//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using RemoteGameHub.Native;

namespace RemoteGameHub.App;

internal enum LogLevel
{
    // Everything. Written only when Debug is on, or before the configuration is read.
    Info,
    // The few things that happen to the machine rather than inside the server — a game started or
    // gone, a screen or a sound device changed under the person sitting at it. Always written.
    Event,
    Warn,
    Error,
    // What nobody caught. Writes the stack of every inner exception as well.
    Crash,
    // Input events. They have a tag of their own or they drown the rest of the file.
    Input,
}

// The only diagnostic channel this application has. There is no window to print to, so a user
// reporting a problem sends this file, and it has to answer the question on its own.
internal static unsafe class Log
{
    private static readonly object Gate = new();

    private static string? _path;
    private static bool _verbose = true;   // until the configuration says otherwise, write everything
    private static int _linesSinceSizeCheck;

    // Who is writing, when it is not the server: the service shares this file, and its lines
    // carry the name so that the two processes can be told apart. Null for the server itself.
    private static string? _source;

    internal static string? Path => _path;

    // Opens the log beside the configuration, else in fallbackDirectory; if neither can be written
    // the application starts anyway.
    internal static void Start(string preferredDirectory, string fallbackDirectory, string version) =>
        Open(preferredDirectory, fallbackDirectory,
             $"--- {AppParameters.Identity.DisplayName} {version} started ---");

    // Moves an open log to another folder without announcing a start: the service follows the
    // signed-in person from session to session, and note says why the lines continue elsewhere.
    internal static void MoveTo(string preferredDirectory, string fallbackDirectory, string note)
    {
        var before = _path;
        Open(preferredDirectory, fallbackDirectory, Line(LogLevel.Event, note));

        if (before is not null && !string.Equals(before, _path, StringComparison.OrdinalIgnoreCase))
            Event($"the log continued from {before}");
    }

    // Names the process on every line from here on. Set by the service, which shares the
    // server's file; the server's own lines carry no name.
    internal static void SetSource(string source) => _source = source;

    private static void Open(string preferredDirectory, string fallbackDirectory, string firstLine)
    {
        foreach (var directory in Distinct(preferredDirectory, fallbackDirectory))
        {
            var candidate = System.IO.Path.Combine(directory, AppParameters.Identity.LogFile);
            try
            {
                Directory.CreateDirectory(directory);
                Rotate(candidate);

                // Written directly rather than through Write(): Write() swallows its errors, so a
                // read-only folder would look like a log that opened fine while this never engaged.
                Append(candidate, firstLine + Environment.NewLine);

                _path = candidate;
                return;
            }
            catch (Exception)
            {
                // Try the next candidate. There is nowhere to report this yet.
            }
        }
    }

    // Puts text after everything already in the file, whoever wrote it. Three processes share the
    // file (launcher, worker, service), so the append is the kernel's, not a seek and a write.
    private static void Append(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        if (!OperatingSystem.IsWindows())
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            stream.Write(bytes);
            return;
        }

        using var file = Kernel32.CreateFile(path, Kernel32.FILE_APPEND_DATA,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE, 0, Kernel32.OPEN_ALWAYS,
            Kernel32.FILE_ATTRIBUTE_NORMAL, 0);
        var created = Marshal.GetLastWin32Error() != Kernel32.ERROR_ALREADY_EXISTS;

        if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

        // The byte-order mark once, at the front of a file this call made; editors read it as UTF-8.
        if (created)
        {
            var preamble = Encoding.UTF8.GetPreamble();
            var marked = new byte[preamble.Length + bytes.Length];
            preamble.CopyTo(marked, 0);
            bytes.CopyTo(marked, preamble.Length);
            bytes = marked;
        }

        fixed (byte* buffer = bytes)
        {
            if (!Kernel32.WriteFile(file, buffer, (uint)bytes.Length, out _, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static IEnumerable<string> Distinct(string first, string second)
    {
        yield return first;
        if (!string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
            yield return second;
    }

    // Applied from Preflight, once the configuration has been read.
    internal static void SetVerbose(bool verbose) => _verbose = verbose;

    internal static void Info(string message) => Write(LogLevel.Info, message);

    // Something the machine did that outlives this server: the screen put into another mode, the
    // sound moved to another device, a game started or gone. Kept whatever Debug says.
    internal static void Event(string message) => Write(LogLevel.Event, message);

    internal static void Warn(string message) => Write(LogLevel.Warn, message);
    internal static void Error(string message) => Write(LogLevel.Error, message);
    internal static void Input(string message) => Write(LogLevel.Input, message);

    // How long a repeating complaint is kept quiet after it has been made once.
    private static readonly TimeSpan RepeatAfter = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, DateTime> LastSaid = new();

    // A warning from somewhere that runs many times a second. Said once, then held for half a
    // minute however often it recurs; subject is what counts as "the same complaint".
    internal static void WarnOccasionally(string subject, string message)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (LastSaid.TryGetValue(subject, out var last) && now - last < RepeatAfter) return;
            LastSaid[subject] = now;
        }

        Write(LogLevel.Warn, message);
    }

    internal static void Error(string message, Exception error) =>
        Write(LogLevel.Error, message + Environment.NewLine + Describe(error));

    internal static void Crash(string message, Exception error) =>
        Write(LogLevel.Crash, message + Environment.NewLine + DescribeDeep(error));

    private static void Write(LogLevel level, string message)
    {
        if (_path is null) return;
        if (!_verbose && level == LogLevel.Info) return;
        if (!_verbose && level == LogLevel.Input) return;

        var text = Line(level, message);

        lock (Gate)
        {
            try
            {
                // Opened and closed per line: the process can be killed at any moment, and a
                // buffered log is an empty log.
                Append(_path, text);

                if (++_linesSinceSizeCheck >= AppParameters.Logging.CheckEveryLines)
                {
                    _linesSinceSizeCheck = 0;
                    Rotate(_path);
                }
            }
            catch (Exception)
            {
                // A log that cannot be written is not worth stopping the stream for.
            }
        }
    }

    // One entry as it appears in the file: the stamp, the level, the source when there is one,
    // and the message with its continuation lines indented under the first.
    private static string Line(LogLevel level, string message)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var tag = level switch
        {
            LogLevel.Info => "INFO ",
            LogLevel.Event => "EVENT",
            LogLevel.Warn => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Crash => "CRASH",
            LogLevel.Input => "INPUT",
            _ => "INFO ",
        };

        var indent = new string(' ', AppParameters.Logging.ContinuationIndent);
        var lines = message.Replace("\r\n", "\n").Split('\n');

        var text = new StringBuilder();
        text.Append(stamp).Append(' ').Append(tag).Append(' ');
        if (_source is not null) text.Append(_source).Append(": ");
        text.AppendLine(lines[0]);
        for (var i = 1; i < lines.Length; i++)
            text.Append(indent).AppendLine(lines[i]);

        return text.ToString();
    }

    // Numbered generations, .log.1 the newest: each is pushed up one number, and whatever falls
    // past MaxRotations is gone rather than kept forever. Failures here are swallowed on purpose.
    private static void Rotate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < AppParameters.Logging.MaxBytes) return;

            var oldest = $"{path}.{AppParameters.Logging.MaxRotations}";
            File.Delete(oldest);

            for (var number = AppParameters.Logging.MaxRotations - 1; number >= 1; number--)
            {
                var from = $"{path}.{number}";
                if (File.Exists(from)) File.Move(from, $"{path}.{number + 1}");
            }

            File.Move(path, $"{path}.1");
        }
        catch (Exception)
        {
        }
    }

    private static string Describe(Exception error) =>
        $"{error.GetType().Name}: {error.Message}";

    private static string DescribeDeep(Exception error)
    {
        var text = new StringBuilder();
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            text.AppendLine($"{current.GetType().FullName}: {current.Message}");
            if (current.StackTrace is { Length: > 0 } stack)
                text.AppendLine(stack);
        }
        return text.ToString().TrimEnd();
    }
}
