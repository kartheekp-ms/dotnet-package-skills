namespace DotnetPackageSkills.Infrastructure;

/// <summary>Invokes the dotnet CLI.</summary>
public sealed class DotnetCli(IProcessRunner runner)
{
    /// <summary>
    /// The dotnet host to invoke. DOTNET_HOST_PATH is set by the SDK when the tool
    /// runs inside a build or from another dotnet command, and points at the exact
    /// host in use — preferring it avoids picking a different dotnet off PATH.
    /// </summary>
    private static string Executable =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)
            ? host
            : "dotnet";

    public ProcessResult Run(params string[] arguments) => runner.Run(Executable, arguments);

    public ProcessResult Run(IReadOnlyList<string> arguments, string? workingDirectory) =>
        runner.Run(Executable, arguments, workingDirectory);
}
