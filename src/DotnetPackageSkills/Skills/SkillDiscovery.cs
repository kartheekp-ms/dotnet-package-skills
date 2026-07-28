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
    /// Each immediate subdirectory of <c>skills/</c> is one skill, and the whole directory
    /// is copied as-is. Nothing inside is read or interpreted — the package author decides
    /// what a skill contains, and this tool's job is only to put it where an agent will
    /// look. A single skill placed directly at <c>skills/SKILL.md</c> is also accepted, and
    /// takes the package id as its name since the author gave it none.
    /// </remarks>
    public static IReadOnlyList<BundledSkill> Discover(string packageDirectory, string packageId, string packageVersion)
    {
        var skillsRoot = FindSkillsFolder(packageDirectory);

        if (skillsRoot is null)
        {
            return [];
        }

        if (File.Exists(Path.Combine(skillsRoot, SkillManifestFileName)))
        {
            return [new BundledSkill(packageId, packageVersion, packageId.ToLowerInvariant(), skillsRoot)];
        }

        return
        [
            .. Directory.EnumerateDirectories(skillsRoot)
                .Select(directory => new { Directory = directory, Name = Path.GetFileName(directory) })
                .Where(candidate => IsSafeSkillName(candidate.Name))
                .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => new BundledSkill(
                    packageId,
                    packageVersion,
                    candidate.Name,
                    candidate.Directory)),
        ];
    }

    /// <summary>
    /// Finds the skills folder case-insensitively, because package contents are authored
    /// on case-insensitive file systems as often as not.
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
    /// Rejects names that would write outside the destination or produce an unusable path.
    /// The name comes from a third-party package, so it is untrusted input even though the
    /// file system has already resolved it to a real directory.
    /// </summary>
    internal static bool IsSafeSkillName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name is not ("." or "..") &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !name.Contains('/') &&
        !name.Contains('\\');
}
