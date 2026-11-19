using System.ComponentModel;
using System.Diagnostics;
using MindAttic.Scheduler.Core;

namespace MindAttic.Scheduler.Cli;

/// <summary>
/// <c>run &lt;job&gt; [--exclusive] -- &lt;command line&gt;</c>: install.ps1 makes this every tracked job's
/// action. It records the run, takes the overnight lock when asked, runs the command on the same
/// hidden console, and exits with the command's own exit code so Task Scheduler's Last Run Result
/// stays accurate. Logging never breaks a job: any trouble recording only reaches stderr.
/// </summary>
internal static class RunCommand
{
    /// <summary>The overnight lock: one arranged job at a time.</summary>
    public const string OvernightMutex = @"Local\MindAttic.Scheduler.Overnight";

    /// <summary>The exit code when the command can't even be started.</summary>
    public const int CouldNotStart = 9009;

    public static int Execute(string[] args, string rawCommandLine)
    {
        var parsed = Parse(args, rawCommandLine);
        var queuedAt = DateTimeOffset.Now;

        RunLog? log = null;
        Guid key = Guid.Empty;
        try
        {
            log = new RunLog(SchedulerDb.FromEnvironment());
            key = log.Start(parsed.Job, parsed.Exclusive, queuedAt);
            if (log.LastError is { } e) Warn($"the run log is spooled ({e.GetType().Name}: {FirstLine(e.Message)})");
        }
        catch (Exception ex) { Warn($"not recorded ({ex.GetType().Name}: {FirstLine(ex.Message)})"); log = null; }

        Mutex? mutex = null;
        var held = false;
        try
        {
            if (parsed.Exclusive)
            {
                mutex = new Mutex(false, OvernightMutex);
                try { held = mutex.WaitOne(TimeSpan.Zero); }
                catch (AbandonedMutexException) { held = true; }
                if (!held)
                {
                    Console.Error.WriteLine($"mindattic-scheduler: {parsed.Job} is waiting for the overnight lock.");
                    try { held = mutex.WaitOne(); }
                    catch (AbandonedMutexException) { held = true; }
                }
                Safe(() => log?.MarkStarted(key, DateTimeOffset.Now));
            }

            var exit = Launch(parsed.Command);
            Safe(() => log?.End(key, DateTimeOffset.Now, exit));
            return exit;
        }
        finally
        {
            if (held) mutex!.ReleaseMutex();
            mutex?.Dispose();
        }
    }

    internal sealed record Parsed(string Job, bool Exclusive, string Command);

    /// <summary>
    /// The job and flags come from <paramref name="args"/>; the command is everything after the
    /// first " -- " in the raw command line, untouched, so its quoting survives exactly.
    /// </summary>
    internal static Parsed Parse(string[] args, string rawCommandLine)
    {
        var sep = Array.IndexOf(args, "--");
        if (sep < 0) throw new UsageException("run needs '-- <command line>'.");
        var before = args.Skip(1).Take(sep - 1).ToList();
        var exclusive = before.RemoveAll(a => string.Equals(a, "--exclusive", StringComparison.OrdinalIgnoreCase)) > 0;
        if (before.Count != 1 || before[0].StartsWith("--", StringComparison.Ordinal))
            throw new UsageException("run needs exactly one job name before '--'.");
        var at = rawCommandLine.IndexOf(" -- ", StringComparison.Ordinal);
        var command = at < 0 ? "" : rawCommandLine[(at + 4)..].Trim();
        if (command.Length == 0) throw new UsageException("run needs a command after '--'.");
        return new Parsed(before[0], exclusive, command);
    }

    /// <summary>'"C:\A B\x.exe" --flag' to ("C:\A B\x.exe", "--flag"); the same split install.ps1 uses.</summary>
    internal static (string Program, string Arguments) Split(string command)
    {
        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            var close = c.IndexOf('"', 1);
            if (close > 0) return (c[1..close], c[(close + 1)..].TrimStart());
        }
        var space = c.IndexOfAny([' ', '\t']);
        return space < 0 ? (c, "") : (c[..space], c[(space + 1)..].TrimStart());
    }

    private static int Launch(string command)
    {
        var (program, arguments) = Split(command);
        // No redirection: the command shares this hidden console, as it did when conhost ran it directly.
        var psi = new ProcessStartInfo(program, arguments) { UseShellExecute = false };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return CouldNotStart;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception ex)
        {
            Console.Error.WriteLine($"mindattic-scheduler: could not start '{program}': {ex.Message}");
            return CouldNotStart;
        }
    }

    private static void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { Warn($"run log write failed ({ex.GetType().Name}: {FirstLine(ex.Message)})"); }
    }

    private static void Warn(string message) => Console.Error.WriteLine($"mindattic-scheduler: {message}");

    internal static string FirstLine(string s) => s.Split('\n')[0].Trim();
}
