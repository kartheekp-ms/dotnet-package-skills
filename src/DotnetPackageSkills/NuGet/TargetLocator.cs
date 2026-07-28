namespace DotnetPackageSkills.NuGet;

/// <summary>Finds the solution or project to inspect when the user does not name one.</summary>
public static class TargetLocator
{
    private static readonly string[] SolutionExtensions = [".slnx", ".sln"];
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];
    private static readonly string[] IgnoredDirectories = ["bin", "obj", ".git", "node_modules", "artifacts"];

    /// <summary>
    /// Resolves an explicit target, or auto-detects one under <paramref name="workingDirectory"/>.
    /// A directory is accepted and searched.
    /// </summary>
    public static string Resolve(string? requested, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Detect(workingDirectory);
        }

        var path = Path.GetFullPath(requested, workingDirectory);

        if (Directory.Exists(path))
        {
            return Detect(path);
        }

        if (!File.Exists(path))
        {
            throw new PackageSkillsException($"--target does not exist: {path}");
        }

        var extension = Path.GetExtension(path);
        if (!SolutionExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) &&
            !ProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new PackageSkillsException(
                $"--target must be a solution or project file, but got '{Path.GetFileName(path)}'. " +
                "Supported extensions: .sln, .slnx, .csproj, .fsproj, .vbproj.");
        }

        return path;
    }

    /// <summary>
    /// Searches for a target, preferring a solution over a project and the top level
    /// over nested directories. A solution covers every project in one pass, which is
    /// almost always what someone means by "my repo".
    /// </summary>
    public static string Detect(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new PackageSkillsException($"Directory does not exist: {directory}");
        }

        foreach (var extensions in new[] { SolutionExtensions, ProjectExtensions })
        {
            var match = EnumerateFiles(directory, extensions, SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        foreach (var extensions in new[] { SolutionExtensions, ProjectExtensions })
        {
            var match = EnumerateFiles(directory, extensions, SearchOption.AllDirectories)
                .Where(path => !IsIgnored(path, directory))
                .FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        throw new PackageSkillsException(
            $"No solution or project found under {directory}. " +
            "Pass one explicitly, for example: --target src/MyApp.sln");
    }

    private static IEnumerable<string> EnumerateFiles(string directory, string[] extensions, SearchOption option)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", option);
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        // Rank by the extension's position in the list, so preference between formats
        // (.slnx ahead of .sln) is not left to how the file names happen to sort.
        return files
            .Select(file => new
            {
                File = file,
                Rank = Array.FindIndex(
                    extensions,
                    extension => extension.Equals(Path.GetExtension(file), StringComparison.OrdinalIgnoreCase)),
            })
            .Where(candidate => candidate.Rank >= 0)
            .OrderBy(candidate => candidate.Rank)
            .ThenBy(candidate => candidate.File, StringComparer.Ordinal)
            .Select(candidate => candidate.File);
    }

    private static bool IsIgnored(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // The file name itself is never a directory match.
        return segments.Take(segments.Length - 1)
            .Any(segment => IgnoredDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }
}
