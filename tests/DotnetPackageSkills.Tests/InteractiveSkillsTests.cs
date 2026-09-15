using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class InteractiveSkillsTests
{
    [Fact]
    public void Install_uses_each_discovered_skill_description_and_preserves_its_identity()
    {
        using var temp = new TempDirectory();
        var first = Skill(temp, "first", "The first package skill.");
        var second = Skill(temp, "second", "The second package skill.");
        var source = File.ReadAllBytes(Path.Combine(first.SourcePath, "SKILL.md"));

        var items = InteractiveSkills.ForInstall([first, second], Names("SECOND"));

        Assert.Collection(
            items,
            item =>
            {
                Assert.Equal("first", item.Name);
                Assert.Equal("Example.Package", item.Package);
                Assert.Equal("1.0.0", item.Version);
                Assert.Equal("The first package skill.", item.Description);
                Assert.Null(item.DescriptionWarning);
                Assert.False(item.Installed);
            },
            item =>
            {
                Assert.Equal("second", item.Name);
                Assert.Equal("The second package skill.", item.Description);
                Assert.True(item.Installed);
            });
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
        Assert.True(item.Installed);
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

        var items = InteractiveSkills.ForInstall([missing, malformed], Names());

        Assert.Collection(
            items,
            item =>
            {
                Assert.Equal("missing", item.Name);
                Assert.Null(item.Description);
                Assert.Null(item.DescriptionWarning);
            },
            item =>
            {
                Assert.Equal("malformed", item.Name);
                Assert.Null(item.Description);
                Assert.False(string.IsNullOrWhiteSpace(item.DescriptionWarning));
            });
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
    public void A_filtered_install_choice_can_only_deselect_skills_that_were_shown()
    {
        using var temp = new TempDirectory();
        var shown = Skill(temp, "shown", "The only skill offered by this filter.");
        var hidden = Skill(temp, "hidden", "Installed from a different selection.");
        var destination = temp.Combine("destination");
        var installer = new SkillInstaller();
        installer.Install(destination, [shown, hidden], dryRun: false);

        var choice = InteractiveSkills.InstallChoice([shown], Names("SHOWN", "hidden"), Names());

        Assert.Empty(choice.Selected);
        Assert.Equal("SHOWN", Assert.Single(choice.Deselected));
        installer.Install(
            destination,
            choice.Selected,
            dryRun: false,
            prune: false,
            deselected: choice.Deselected);
        Assert.False(Directory.Exists(Path.Combine(destination, "shown")));
        Assert.True(File.Exists(Path.Combine(destination, "hidden", "SKILL.md")));
        Assert.Equal("hidden", Assert.Single(InstallManifest.Load(destination).EnumerateSkills()).Skill);
    }

    [Fact]
    public void Install_choice_retains_selected_skills_and_never_invents_unknown_ones()
    {
        using var temp = new TempDirectory();
        var first = Skill(temp, "first", "One.");
        var second = Skill(temp, "second", "Two.");

        var choice = InteractiveSkills.InstallChoice(
            [first, second],
            Names("first", "unshown"),
            Names("FIRST", "second", "unknown"));

        Assert.Equal([first, second], choice.Selected);
        Assert.Empty(choice.Deselected);
    }

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
