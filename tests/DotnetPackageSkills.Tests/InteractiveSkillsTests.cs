using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class InteractiveSkillsTests
{
    [Fact]
    public void Install_offers_only_skills_that_are_not_installed_with_their_descriptions()
    {
        using var temp = new TempDirectory();
        var first = Skill(temp, "first", "The first package skill.");
        var second = Skill(temp, "second", "The second package skill.");
        var source = File.ReadAllBytes(Path.Combine(first.SourcePath, "SKILL.md"));

        var items = InteractiveSkills.ForInstall([first, second], Owners("SECOND"));

        var item = Assert.Single(items);
        Assert.Equal("first", item.Name);
        Assert.Equal("Example.Package", item.Package);
        Assert.Equal("1.0.0", item.Version);
        Assert.Equal("The first package skill.", item.Description);
        Assert.Null(item.DescriptionWarning);
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(first.SourcePath, "SKILL.md")));
    }

    [Fact]
    public void Uninstall_reads_the_installed_copy_and_only_offers_manifest_owned_skills()
    {
        using var temp = new TempDirectory();
        var skill = Skill(temp, "example", "Description shipped with the installed version.");
        var destination = temp.Combine("destination");
        new SkillInstaller().Install(destination, [skill], dryRun: false);
        File.WriteAllText(
            Path.Combine(skill.SourcePath, "SKILL.md"),
            "---\ndescription: A different description now in the cache.\n---\n");
        var handwritten = Path.Combine(destination, "team-conventions");
        Directory.CreateDirectory(handwritten);
        File.WriteAllText(Path.Combine(handwritten, "SKILL.md"), "---\ndescription: Our own skill.\n---\n");
        var manifest = File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName));

        var tracked = SkillInstallService.InstalledSkills(destination, temp.Path);
        var items = InteractiveSkills.ForUninstall(tracked, destination);

        var item = Assert.Single(items);
        Assert.Equal("example", item.Name);
        Assert.Equal("Description shipped with the installed version.", item.Description);
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName)));
        Assert.True(File.Exists(Path.Combine(handwritten, "SKILL.md")));
    }

    [Fact]
    public void Missing_or_invalid_descriptions_do_not_remove_skills_from_the_picker()
    {
        using var temp = new TempDirectory();
        var missing = Skill(temp, "missing", "unused");
        var malformed = Skill(temp, "malformed", "unused");
        File.WriteAllText(Path.Combine(missing.SourcePath, "SKILL.md"), "# No frontmatter\n");
        File.WriteAllText(Path.Combine(malformed.SourcePath, "SKILL.md"), "---\ndescription: [broken\n---\n");

        var items = InteractiveSkills.ForInstall([missing, malformed], []);

        Assert.Equal(2, items.Count);
        var absent = Assert.Single(items, item => item.Name == "missing");
        Assert.Null(absent.Description);
        Assert.Null(absent.DescriptionWarning);
        var invalid = Assert.Single(items, item => item.Name == "malformed");
        Assert.Null(invalid.Description);
        Assert.False(string.IsNullOrWhiteSpace(invalid.DescriptionWarning));
    }

    [Fact]
    public void An_installed_skill_whose_file_is_missing_can_still_be_selected_for_removal()
    {
        using var temp = new TempDirectory();
        var skill = Skill(temp, "example", "An installed skill.");
        var destination = temp.Combine("destination");
        var installer = new SkillInstaller();
        installer.Install(destination, [skill], dryRun: false);
        File.Delete(Path.Combine(destination, "example", "SKILL.md"));

        var items = InteractiveSkills.ForUninstall(
            SkillInstallService.InstalledSkills(destination, temp.Path),
            destination);

        var item = Assert.Single(items);
        Assert.Equal("example", item.Name);
        Assert.Null(item.Description);
        Assert.False(string.IsNullOrWhiteSpace(item.DescriptionWarning));
        var removed = installer.Uninstall(destination, null, null, dryRun: false, only: [item.Name]);
        Assert.Equal("example", Assert.Single(removed).Skill);
    }

    [Fact]
    public void Description_loading_does_not_bypass_a_corrupt_ownership_manifest()
    {
        using var temp = new TempDirectory();
        var destination = temp.CreateDirectory("destination");
        var manifest = Path.Combine(destination, InstallManifest.FileName);
        const string conflict = "<<<<<<< HEAD\n{}\n=======\n{}\n>>>>>>> branch";
        File.WriteAllText(manifest, conflict);

        Assert.Throws<PackageSkillsException>(() =>
            InteractiveSkills.ForUninstall(
                SkillInstallService.InstalledSkills(destination, temp.Path),
                destination));

        Assert.Equal(conflict, File.ReadAllText(manifest));
    }

    [Fact]
    public void An_install_choice_never_refreshes_or_removes_an_installed_skill()
    {
        using var temp = new TempDirectory();
        var installedSkill = Skill(temp, "installed", "Already installed.");
        var fresh = Skill(temp, "fresh", "Not installed yet.");
        var destination = temp.Combine("destination");
        var installer = new SkillInstaller();
        installer.Install(destination, [installedSkill], dryRun: false);
        var edited = temp.CreateFile("destination/installed/SKILL.md", "edited locally");
        var installed = SkillInstallService.InstalledSkills(destination, temp.Path);

        var items = InteractiveSkills.ForInstall([installedSkill, fresh], installed);
        var choice = InteractiveSkills.InstallChoice(
            [installedSkill, fresh], installed, items, Names("installed", "fresh"));
        var outcome = installer.Install(
            destination,
            choice.Selected,
            dryRun: false,
            offered: new Dictionary<string, string>(),
            expectedInstalled: choice.ExpectedInstalled);

        Assert.Equal("fresh", Assert.Single(items).Name);
        Assert.Equal("fresh", Assert.Single(choice.Selected).SkillName);
        Assert.Empty(outcome.Removed);
        Assert.Equal("edited locally", File.ReadAllText(edited));
        Assert.Equal(
            ["fresh", "installed"],
            InstallManifest.Load(destination).EnumerateSkills().Select(entry => entry.Skill).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_install_choice_takes_only_ticked_skills_that_were_shown()
    {
        using var temp = new TempDirectory();
        var first = Skill(temp, "first", "One.");
        var second = Skill(temp, "second", "Two.");

        var installed = Owners("first", "unshown");
        var items = InteractiveSkills.ForInstall([first, second], installed);
        var choice = InteractiveSkills.InstallChoice(
            [first, second], installed, items, Names("FIRST", "second", "unknown"));

        Assert.Equal([second], choice.Selected);
        Assert.Same(installed, choice.ExpectedInstalled);
    }

    [Fact]
    public void Another_packages_same_named_skill_is_not_offered()
    {
        using var temp = new TempDirectory();
        var candidate = Skill(temp, "shared", "A candidate from Example.Package.");
        var installed = new[] { new TrackedSkill("other.package", "2.0.0", "shared") };

        var items = InteractiveSkills.ForInstall([candidate], installed);
        var choice = InteractiveSkills.InstallChoice([candidate], installed, items, Names("shared"));

        Assert.Empty(items);
        Assert.Empty(choice.Selected);
    }

    private static IReadOnlyList<TrackedSkill> Owners(params string[] names) =>
        [.. names.Select(name => new TrackedSkill("Example.Package", "1.0.0", name))];

    private static HashSet<string> Names(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    private static BundledSkill Skill(TempDirectory temp, string name, string description)
    {
        var package = temp.CreatePackageWithSkill("Example.Package", "1.0.0", name);
        var directory = Path.Combine(package, "skills", name);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\n# Body\n");
        return new BundledSkill("Example.Package", "1.0.0", name, directory, name);
    }
}
