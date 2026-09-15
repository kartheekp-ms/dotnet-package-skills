using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Cli;

internal static class InteractiveSkills
{
    public static IReadOnlyList<SkillPickerItem> ForInstall(
        IReadOnlyList<BundledSkill> skills,
        IReadOnlySet<string> installed) =>
        [
            .. skills.Select(skill => Describe(
                skill.RelativePath,
                skill.PackageId,
                skill.PackageVersion,
                installed.Contains(skill.RelativePath),
                skill.SourcePath)),
        ];

    public static IReadOnlyList<SkillPickerItem> ForUninstall(
        IReadOnlyList<TrackedSkill> skills,
        string destination) =>
        [
            .. skills.Select(skill => Describe(
                skill.Skill,
                skill.Package,
                skill.Version,
                installed: true,
                Path.Combine(destination, skill.Skill))),
        ];

    public static SkillChoice InstallChoice(
        IReadOnlyList<BundledSkill> skills,
        IReadOnlySet<string> installed,
        IReadOnlySet<string> selected)
    {
        var shown = skills.Select(skill => skill.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new SkillChoice(
            [.. skills.Where(skill => selected.Contains(skill.RelativePath))],
            [.. installed.Where(name => shown.Contains(name) && !selected.Contains(name))]);
    }

    private static SkillPickerItem Describe(
        string name,
        string package,
        string version,
        bool installed,
        string skillDirectory)
    {
        var metadata = SkillDescriptionReader.Read(skillDirectory);
        return new SkillPickerItem(
            name,
            package,
            version,
            installed,
            metadata.Description,
            metadata.Warning);
    }
}
