using DotnetPackageSkills.NuGet;

namespace DotnetPackageSkills.Skills;

/// <summary>A skill found inside an extracted NuGet package.</summary>
/// <param name="PackageId">Package id as reported by NuGet, in its original casing.</param>
/// <param name="PackageVersion">Resolved version as reported by NuGet.</param>
/// <param name="SkillName">Folder name of the skill inside the package's <c>skills/</c> directory.</param>
/// <param name="SourcePath">Absolute path to the skill folder in the global packages cache.</param>
public sealed record BundledSkill(
    string PackageId,
    string PackageVersion,
    string SkillName,
    string SourcePath)
{
    /// <summary>
    /// Destination path relative to the skills root, always with forward slashes so the
    /// manifest is stable across operating systems.
    /// </summary>
    /// <remarks>
    /// Package id and version are part of the path deliberately: two packages can ship a
    /// skill with the same name without colliding, and anyone reading the tree can tell
    /// where a skill came from and which version it documents.
    /// </remarks>
    public string RelativePath =>
        $"{PackageId.ToLowerInvariant()}/{PackagePathResolver.NormalizeVersion(PackageVersion)}/{SkillName}";
}
