namespace DotnetPackageSkills.Skills;

/// <summary>Finds skills that a package author bundled at <c>skills/</c> in the package root.</summary>
public static class SkillDiscovery
{
    public const string SkillsFolderName = "skills";
    public const string SkillManifestFileName = "SKILL.md";

    /// <summary>
    /// Enumerates the skills inside an extracted package.
    /// </summary>
    /// <remarks>
    /// Each immediate subdirectory of <c>skills/</c> that contains <c>SKILL.md</c> is one skill,
    /// and the whole directory is copied as-is. Its authored folder name is also its destination
    /// folder name. Nothing inside the skill is read or interpreted.
    /// </remarks>
    public static IReadOnlyList<BundledSkill> Discover(string packageDirectory, string packageId, string packageVersion)
    {
        var skillsRoot = FindSkillsFolder(packageDirectory);

        if (skillsRoot is null)
        {
            return [];
        }

        var candidates = Directory.EnumerateDirectories(skillsRoot)
            .Select(directory => new { Directory = directory, Name = Path.GetFileName(directory) })
            .Where(candidate =>
                IsSafeSkillName(candidate.Name) &&
                File.Exists(Path.Combine(candidate.Directory, SkillManifestFileName)))
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .ToList();

        return
        [
            .. candidates.Select(candidate => new BundledSkill(
                packageId,
                packageVersion,
                candidate.Name,
                candidate.Directory,
                candidate.Name)),
        ];
    }

    /// <summary>
    /// Finds the skills folder case-insensitively, because package contents are authored on
    /// case-insensitive file systems as often as not.
    /// </summary>
    private static string? FindSkillsFolder(string packageDirectory)
    {
        if (!Directory.Exists(packageDirectory))
        {
            return null;
        }

        return Directory.EnumerateDirectories(packageDirectory)
            .FirstOrDefault(directory =>
                string.Equals(Path.GetFileName(directory), SkillsFolderName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Rejects names that would write outside the destination or produce an unusable path. The
    /// name comes from a third-party package, so it is untrusted input even though the file
    /// system has already resolved it to a real directory.
    /// </summary>
    internal static bool IsSafeSkillName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name is not ("." or "..") &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !name.Contains('/') &&
        !name.Contains('\\');
}
