using System.Diagnostics;

namespace MindAttic.Scheduler.Core;

/// <summary>Runs a PowerShell script and captures its output, the one way this app talks to
/// scheduler.ps1/install.ps1 instead of reimplementing their Task Scheduler logic in C#.</summary>
public static class PowerShellRunner
{
    /// <summary>Runs <paramref name="scriptPath"/> with <paramref name="arguments"/>, waits for it to exit.</summary>
    public static PowerShellResult Run(string scriptPath, params string[] arguments)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start powershell.exe for '{scriptPath}'.");
        // Drain stderr concurrently: reading stdout to the end first deadlocks once the script
        // writes more to stderr than its pipe buffer holds (it blocks on stderr, we block on stdout).
        var stdErrTask = process.StandardError.ReadToEndAsync();
        string stdOut = process.StandardOutput.ReadToEnd();
        string stdErr = stdErrTask.GetAwaiter().GetResult();
        process.WaitForExit();
        return new PowerShellResult(process.ExitCode, stdOut, stdErr);
    }
}

/// <summary>The outcome of a <see cref="PowerShellRunner.Run"/> call.</summary>
public sealed record PowerShellResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Throws, with the script's own stderr, if the run didn't exit 0.</summary>
    public PowerShellResult EnsureSuccess(string what)
    {
        if (ExitCode != 0) throw new InvalidOperationException($"{what} failed: {StandardError.Trim()}");
        return this;
    }
}
