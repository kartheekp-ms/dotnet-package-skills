using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Cli;

internal sealed record UninstallChoice(
    IReadOnlyCollection<string> Selected,
    IReadOnlyCollection<TrackedSkill> ExpectedInstalled);

internal static class InteractiveSkills
{
    /// <summary>
    /// Checklist items for install, which only adds. Installed skills are never offered, so
    /// nothing on the list can refresh, replace or remove a skill the user already has.
    /// </summary>
    public static IReadOnlyList<SkillPickerItem> ForInstall(
        IReadOnlyList<BundledSkill> candidates,
        IReadOnlyCollection<TrackedSkill> installed)
    {
        var tracked = installed.Select(entry => entry.Skill).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. candidates
                .Where(skill => !tracked.Contains(skill.RelativePath))
                .Select(skill => Describe(skill.RelativePath, skill.PackageId, skill.PackageVersion, skill.SourcePath))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.Ordinal),
        ];
    }

    public static IReadOnlyList<SkillPickerItem> ForUninstall(
        IReadOnlyList<TrackedSkill> skills,
        string destination) =>
        [
            .. skills.Select(skill => Describe(
                skill.Skill,
                skill.Package,
                skill.Version,
                Path.Combine(destination, skill.Skill))),
        ];

    /// <summary>The checked skills that were actually shown. Nothing else is installed or changed.</summary>
    public static SkillChoice InstallChoice(
        IReadOnlyList<BundledSkill> candidates,
        IReadOnlyCollection<TrackedSkill> installed,
        IReadOnlyList<SkillPickerItem> shown,
        IReadOnlySet<string> selected) =>
        new(
        [
            .. candidates.Where(skill => selected.Contains(skill.RelativePath) &&
                shown.Any(item =>
                    item.Name.Equals(skill.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                    item.Package.Equals(skill.PackageId, StringComparison.OrdinalIgnoreCase))),
        ])
        {
            ExpectedInstalled = installed,
        };

    private static SkillPickerItem Describe(
        string name,
        string package,
        string version,
        string skillDirectory)
    {
        var metadata = SkillDescriptionReader.Read(skillDirectory);
        return new SkillPickerItem(
            name,
            package,
            version,
            metadata.Description,
            metadata.Warning);
    }
}
