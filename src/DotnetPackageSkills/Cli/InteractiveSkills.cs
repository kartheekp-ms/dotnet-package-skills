using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Cli;

internal sealed record UninstallChoice(
    IReadOnlyCollection<string> Selected,
    IReadOnlyCollection<TrackedSkill> ExpectedInstalled);

internal static class InteractiveSkills
{
    public static IReadOnlyList<SkillPickerItem> ForInstall(
        IReadOnlyList<BundledSkill> skills,
        IReadOnlyList<TrackedSkill> installed,
        string destination,
        bool includeRetained = false)
    {
        var items = new List<SkillPickerItem>();
        foreach (var skill in skills)
        {
            var owner = installed.FirstOrDefault(entry =>
                entry.Skill.Equals(skill.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (owner is not null && !SkillInstaller.HasSameOwner(owner, skill))
            {
                continue;
            }

            items.Add(Describe(
                skill.RelativePath,
                skill.PackageId,
                skill.PackageVersion,
                owner is not null,
                skill.SourcePath));
        }

        if (includeRetained)
        {
            var available = items.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in installed.Where(entry => !available.Contains(entry.Skill)))
            {
                items.Add(Describe(
                    entry.Skill, entry.Package, entry.Version, installed: true,
                    Path.Combine(destination, entry.Skill)) with
                {
                    Retained = true,
                });
            }
        }

        return [.. items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.Ordinal)];
    }

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
        IReadOnlyList<TrackedSkill> installed,
        IReadOnlyList<SkillPickerItem> shown,
        IReadOnlySet<string> selected)
    {
        return new SkillChoice(
            [.. skills.Where(skill => selected.Contains(skill.RelativePath) &&
                shown.Any(item => !item.Retained &&
                    item.Name.Equals(skill.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                    item.Package.Equals(skill.PackageId, StringComparison.OrdinalIgnoreCase)))],
            [.. installed.Where(entry => !selected.Contains(entry.Skill) &&
                shown.Any(item => item.Installed &&
                    item.Name.Equals(entry.Skill, StringComparison.OrdinalIgnoreCase) &&
                    item.Package.Equals(entry.Package, StringComparison.OrdinalIgnoreCase)))
                .Select(entry => entry.Skill)])
        {
            ExpectedInstalled = installed,
        };
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
