using System.Diagnostics;

namespace DotnetPackageSkills.Infrastructure;

/// <summary>Result of running an external process to completion.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>
    /// Diagnostics text for error messages: tools write failures to stderr, but the
    /// dotnet CLI frequently reports MSBuild and NuGet errors on stdout instead.
    /// </summary>
    public string Diagnostics =>
        string.IsNullOrWhiteSpace(StandardError) ? StandardOutput.Trim() : StandardError.Trim();
}

/// <summary>Runs external processes. Abstracted so command logic is testable without spawning dotnet.</summary>
public interface IProcessRunner
{
    ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null);
}

/// <summary>Thrown when a process cannot be started or does not finish in time.</summary>
public sealed class ProcessExecutionException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class ProcessRunner(TimeSpan? timeout = null) : IProcessRunner
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(5);

    public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new ProcessExecutionException(
                $"Could not start '{fileName}'. Make sure the .NET SDK is installed and on PATH " +
                "(https://dotnet.microsoft.com/download).", ex);
        }

        // Read both streams concurrently before waiting. Draining one to completion
        // first deadlocks as soon as the other fills its pipe buffer, which dotnet
        // restore output does routinely.
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
        {
            TryKill(process);
            throw new ProcessExecutionException(
                $"'{fileName} {string.Join(' ', arguments)}' did not finish within {_timeout.TotalSeconds:0} seconds.");
        }

        // The overload that takes a timeout does not wait for the async output
        // readers to drain, so the parameterless call is needed for complete output.
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, standardOutput.Result, standardError.Result);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // The process already exited or cannot be killed; nothing useful to do.
        }
    }
}
