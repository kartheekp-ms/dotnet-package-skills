using DotnetPackageSkills.Infrastructure;

namespace DotnetPackageSkills.NuGet;

/// <summary>Locates the NuGet global packages folder, where restore extracts packages.</summary>
public sealed class GlobalPackagesLocator(DotnetCli dotnet)
{
    private const string Label = "global-packages:";

    /// <summary>
    /// Resolves the folder, honouring an explicit override, then NUGET_PACKAGES, then
    /// whatever the CLI reports (which is the only thing that accounts for a
    /// <c>globalPackagesFolder</c> set in a nuget.config).
    /// </summary>
    /// <param name="workingDirectory">
    /// Where to ask from. This matters: nuget.config discovery walks up from the current
    /// directory, so asking from outside the repo silently ignores a repo-level config.
    /// </param>
    public string Locate(string? overridePath, string workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var resolved = Path.GetFullPath(overridePath, workingDirectory);
            return Directory.Exists(resolved)
                ? resolved
                : throw new PackageSkillsException($"--global-packages does not exist: {resolved}");
        }

        if (Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } fromEnvironment &&
            Directory.Exists(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        return FromCli(workingDirectory);
    }

    private string FromCli(string workingDirectory)
    {
        var result = dotnet.Run(["nuget", "locals", "global-packages", "--list"], workingDirectory);

        if (result.ExitCode != 0)
        {
            throw new PackageSkillsException(
                $"""
                 Could not determine the NuGet global packages folder.
                 'dotnet nuget locals global-packages --list' failed with exit code {result.ExitCode}:
                 {result.Diagnostics}
                 """);
        }

        var path = ParseListOutput(result.StandardOutput);

        if (path is null)
        {
            throw new PackageSkillsException(
                $"""
                 Could not find the global packages path in the output of
                 'dotnet nuget locals global-packages --list':
                 {result.StandardOutput.Trim()}
                 """);
        }

        if (!Directory.Exists(path))
        {
            throw new PackageSkillsException(
                $"""
                 NuGet reports its global packages folder as '{path}', but that directory
                 does not exist. Restore the project first — restore is what creates it.
                 """);
        }

        return path;
    }

    /// <summary>
    /// Extracts the path from CLI output. The shape has drifted across SDK versions
    /// ("global-packages: /path" today, "info : global-packages: /path" on older ones),
    /// so this keys off the label rather than the line's position or prefix.
    /// </summary>
    internal static string? ParseListOutput(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var index = line.IndexOf(Label, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var value = line[(index + Label.Length)..].Trim();
            if (value.Length > 0)
            {
                return Path.GetFullPath(value);
            }
        }

        return null;
    }
}
