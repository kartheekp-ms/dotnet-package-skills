using System.Text.Json.Serialization;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Cli;

/// <summary>One skill, named the same way everywhere <c>--json</c> mentions a skill.</summary>
/// <remarks>
/// Every array in every command spells the same three concepts these three ways. A script that
/// reads <c>skills</c> can read <c>removed</c> and <c>skipped</c> with the same code.
/// </remarks>
public sealed record SkillJson
{
    [JsonPropertyName("packageId")]
    public required string PackageId { get; init; }

    [JsonPropertyName("packageVersion")]
    public required string PackageVersion { get; init; }

    [JsonPropertyName("skillName")]
    public required string SkillName { get; init; }

    /// <summary>Where the skill was read from. Absent once it is gone from the cache.</summary>
    [JsonPropertyName("sourcePath")]
    public string? SourcePath { get; init; }

    /// <summary>Where the skill sits under the destination, with forward slashes.</summary>
    [JsonPropertyName("relativePath")]
    public string? RelativePath { get; init; }

    /// <summary>Why a skill was passed over. Only ever set on <c>skipped</c>.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>What <c>install</c> and <c>list</c> emit under <c>--json</c>.</summary>
public sealed record InstallReportJson
{
    /// <summary>The solution or project inspected. Absent when packages were named outright.</summary>
    [JsonPropertyName("target")]
    public string? Target { get; init; }

    [JsonPropertyName("globalPackagesFolder")]
    public required string GlobalPackagesFolder { get; init; }

    [JsonPropertyName("destination")]
    public required string Destination { get; init; }

    [JsonPropertyName("packagesScanned")]
    public required int PackagesScanned { get; init; }

    [JsonPropertyName("dryRun")]
    public required bool DryRun { get; init; }

    [JsonPropertyName("skills")]
    public required IReadOnlyList<SkillJson> Skills { get; init; }

    [JsonPropertyName("skillsDiscovered")]
    public required int SkillsDiscovered { get; init; }

    [JsonPropertyName("removed")]
    public required IReadOnlyList<SkillJson> Removed { get; init; }

    [JsonPropertyName("skipped")]
    public required IReadOnlyList<SkillJson> Skipped { get; init; }

    [JsonPropertyName("notOnDisk")]
    public required IReadOnlyList<string> NotOnDisk { get; init; }
}

/// <summary>What <c>uninstall</c> emits under <c>--json</c>.</summary>
public sealed record UninstallReportJson
{
    [JsonPropertyName("destination")]
    public required string Destination { get; init; }

    [JsonPropertyName("dryRun")]
    public required bool DryRun { get; init; }

    [JsonPropertyName("removed")]
    public required IReadOnlyList<SkillJson> Removed { get; init; }
}

/// <summary>
/// Projects results onto the <c>--json</c> contract.
/// </summary>
/// <remarks>
/// The commands used to serialize their result records straight to stdout, which quietly made
/// every domain rename a breaking change and published two vocabularies at once: skills came out
/// of <see cref="BundledSkill"/> as packageId/packageVersion/skillName, while removed came out of
/// <see cref="TrackedSkill"/> as package/version/skill. Same three concepts, two spellings, so
/// anything reading both needed two mappers.
///
/// Renaming <see cref="TrackedSkill"/> was not the fix. It mirrors the manifest file, whose own
/// package/version/skills wording is a stored format: rename it and manifests already on disk stop
/// being understood. The two vocabularies are both correct for their own format, and the mistake
/// was letting either one reach stdout by accident. So the wire shape lives here, spelled out, and
/// the manifest keeps its wording.
/// </remarks>
public static class JsonReport
{
    public static InstallReportJson For(InstallResult result) => new()
    {
        Target = result.Target,
        GlobalPackagesFolder = result.GlobalPackagesFolder,
        Destination = result.Destination,
        PackagesScanned = result.PackagesScanned,
        DryRun = result.DryRun,
        Skills = [.. result.Skills.Select(Describe)],
        SkillsDiscovered = result.SkillsDiscovered,
        Removed = [.. result.Removed.Select(Describe)],
        Skipped = [.. result.Skipped.Select(Describe)],
        NotOnDisk = result.NotOnDisk,
    };

    public static UninstallReportJson ForUninstall(
        IReadOnlyList<TrackedSkill> removed,
        string destination,
        bool dryRun) => new()
        {
            Destination = destination,
            DryRun = dryRun,
            Removed = [.. removed.Select(Describe)],
        };

    private static SkillJson Describe(BundledSkill skill) => new()
    {
        PackageId = skill.PackageId,
        PackageVersion = skill.PackageVersion,
        SkillName = skill.SkillName,
        SourcePath = skill.SourcePath,
        RelativePath = skill.RelativePath,
    };

    private static SkillJson Describe(SkippedSkill skill) => new()
    {
        PackageId = skill.PackageId,
        PackageVersion = skill.PackageVersion,
        SkillName = skill.SkillName,
        RelativePath = skill.RelativePath,
        Reason = skill.Reason,
    };

    // A removed skill has no source path, because the point of removing it is that it is no
    // longer anywhere to be read from.
    private static SkillJson Describe(TrackedSkill skill) => new()
    {
        PackageId = skill.Package,
        PackageVersion = skill.Version,
        SkillName = skill.Skill,
        RelativePath = skill.Skill,
    };
}
