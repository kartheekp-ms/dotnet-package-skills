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
/// <remarks>
/// The destination path is decided during discovery rather than derived here, because it depends
/// on how many skills the package ships — see <see cref="SkillDiscovery"/>.
/// </remarks>
public sealed record BundledSkill(
    string PackageId,
    string PackageVersion,
    string SkillName,
    string SourcePath,
    string RelativePath);
