namespace DotnetPackageSkills.Skills;

/// <summary>A skill found inside an extracted NuGet package.</summary>
/// <param name="PackageId">Package id as reported by NuGet, in its original casing.</param>
/// <param name="PackageVersion">Resolved version as reported by NuGet.</param>
/// <param name="SkillName">Folder name of the skill inside the package's <c>skills/</c> directory.</param>
/// <param name="SourcePath">Absolute path to the skill folder in the global packages cache.</param>
/// <param name="RelativePath">
/// Destination path relative to the skills root, always with forward slashes so the manifest is
/// stable across operating systems.
/// </param>
public sealed record BundledSkill(
    string PackageId,
    string PackageVersion,
    string SkillName,
    string SourcePath,
    string RelativePath);

/// <summary>A package skill that was not copied because its destination path collided.</summary>
public sealed record SkippedSkill(
    string RelativePath,
    string PackageId,
    string PackageVersion,
    string SkillName,
    string Reason);
