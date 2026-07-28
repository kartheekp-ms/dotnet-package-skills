using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class SkillInstallerTests
{
    private readonly SkillInstaller _installer = new();

    /// <summary>
    /// Builds a skill whose destination is the single-skill layout, <c>package/version/</c>.
    /// Pass <paramref name="nested"/> to get the multi-skill layout instead.
    /// </summary>
    private static BundledSkill Skill(
        TempDirectory temp,
        string packageId,
        string version,
        string skillName,
        bool nested = false)
    {
        var packageDirectory = temp.CreatePackageWithSkill(packageId, version, skillName);
        var root = $"{packageId.ToLowerInvariant()}/{version}";

        return new BundledSkill(
            packageId,
            version,
            skillName,
            Path.Combine(packageDirectory, "skills", skillName),
            nested ? $"{root}/{skillName}" : root);
    }

    [Fact]
    public void Install_copies_a_skill_to_package_version_skill()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);

        Assert.True(File.Exists(Path.Combine(destination, "mockly", "1.10.0", "SKILL.md")));
    }

    [Fact]
    public void Install_copies_nested_files_such_as_references()
    {
        using var temp = new TempDirectory();
        var skill = Skill(temp, "Contoso.Widgets", "2.3.0", "widget-usage");
        temp.CreateFile("packages/contoso.widgets/2.3.0/skills/widget-usage/references/batching.md", "rules");

        _installer.Install(temp.Combine("dest"), [skill], dryRun: false);

        Assert.Equal(
            "rules",
            File.ReadAllText(temp.Combine("dest", "contoso.widgets", "2.3.0", "references", "batching.md")));
    }

    [Fact]
    public void Install_with_dryRun_writes_nothing()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        var outcome = _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: true);

        Assert.Single(outcome.Installed);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void Install_removes_the_previous_version_when_a_package_is_upgraded()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);
        var outcome = _installer.Install(destination, [Skill(temp, "Mockly", "1.11.0", "mockly")], dryRun: false);

        Assert.False(Directory.Exists(Path.Combine(destination, "mockly", "1.10.0")));
        Assert.True(Directory.Exists(Path.Combine(destination, "mockly", "1.11.0")));
        Assert.Equal("mockly/1.10.0", Assert.Single(outcome.Removed).Path);
    }

    [Fact]
    public void Install_removes_skills_from_a_package_that_is_no_longer_referenced()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        _installer.Install(
            destination,
            [Skill(temp, "Mockly", "1.10.0", "mockly"), Skill(temp, "Contoso.Widgets", "2.3.0", "widget-usage")],
            dryRun: false);

        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);

        Assert.False(Directory.Exists(Path.Combine(destination, "contoso.widgets")));
    }

    [Fact]
    public void Install_survives_a_package_gaining_a_second_skill_at_the_same_version()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        // One skill installs flat, at mockly/1.10.0/.
        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);

        // The same version now ships two, so both nest one level deeper. The stale entry is
        // now the *parent* of what is being written, so removing it after copying would
        // delete the new skills.
        var outcome = _installer.Install(
            destination,
            [
                Skill(temp, "Mockly", "1.10.0", "mockly", nested: true),
                Skill(temp, "Mockly", "1.10.0", "sub-skill", nested: true),
            ],
            dryRun: false);

        Assert.True(File.Exists(Path.Combine(destination, "mockly", "1.10.0", "mockly", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(destination, "mockly", "1.10.0", "sub-skill", "SKILL.md")));

        // The old flat copy at mockly/1.10.0/SKILL.md must not survive alongside them.
        Assert.False(File.Exists(Path.Combine(destination, "mockly", "1.10.0", "SKILL.md")));
        Assert.Equal("mockly/1.10.0", Assert.Single(outcome.Removed).Path);
    }

    [Fact]
    public void Install_survives_a_package_dropping_back_to_a_single_skill()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        _installer.Install(
            destination,
            [
                Skill(temp, "Mockly", "1.10.0", "mockly", nested: true),
                Skill(temp, "Mockly", "1.10.0", "sub-skill", nested: true),
            ],
            dryRun: false);

        // Back to one skill, so it goes flat again and the nested folders must be gone.
        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);

        Assert.True(File.Exists(Path.Combine(destination, "mockly", "1.10.0", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(destination, "mockly", "1.10.0", "sub-skill")));
        Assert.False(Directory.Exists(Path.Combine(destination, "mockly", "1.10.0", "mockly")));
    }

    [Fact]
    public void Install_never_touches_skills_it_did_not_install()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        // Pruning is driven by the manifest, so a hand-authored skill sitting alongside
        // package-provided ones has to survive every sync.
        Directory.CreateDirectory(destination);
        var handAuthored = Path.Combine(destination, "our-own-skill");
        Directory.CreateDirectory(handAuthored);
        File.WriteAllText(Path.Combine(handAuthored, "SKILL.md"), "ours");

        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);
        _installer.Install(destination, [], dryRun: false);

        Assert.True(File.Exists(Path.Combine(handAuthored, "SKILL.md")));
    }

    [Fact]
    public void Install_replaces_files_that_a_newer_package_version_dropped()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        var first = Skill(temp, "Mockly", "1.10.0", "mockly");
        File.WriteAllText(Path.Combine(first.SourcePath, "obsolete.md"), "gone in the next version");
        _installer.Install(destination, [first], dryRun: false);

        // Reinstalling the same version from a source that no longer has the file must
        // not leave the stale copy behind.
        File.Delete(Path.Combine(first.SourcePath, "obsolete.md"));
        _installer.Install(destination, [first], dryRun: false);

        Assert.False(File.Exists(Path.Combine(destination, "mockly", "1.10.0", "obsolete.md")));
    }

    [Fact]
    public void Install_clears_the_read_only_flag_that_restore_puts_on_cached_files()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        var skill = Skill(temp, "Mockly", "1.10.0", "mockly");

        var source = Path.Combine(skill.SourcePath, "SKILL.md");
        File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly);

        try
        {
            _installer.Install(destination, [skill], dryRun: false);

            var copied = Path.Combine(destination, "mockly", "1.10.0", "SKILL.md");
            Assert.False(File.GetAttributes(copied).HasFlag(FileAttributes.ReadOnly));

            // The real point: a second sync must be able to overwrite the copy.
            _installer.Install(destination, [skill], dryRun: false);
        }
        finally
        {
            File.SetAttributes(source, File.GetAttributes(source) & ~FileAttributes.ReadOnly);
        }
    }

    [Fact]
    public void Uninstall_removes_everything_it_installed_including_the_manifest()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);

        var removed = _installer.Uninstall(destination, packageId: null, packageVersion: null, dryRun: false);

        Assert.Single(removed);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void Uninstall_can_target_a_single_package()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [Skill(temp, "Mockly", "1.10.0", "mockly"), Skill(temp, "Contoso.Widgets", "2.3.0", "widget-usage")],
            dryRun: false);

        _installer.Uninstall(destination, packageId: "mockly", packageVersion: null, dryRun: false);

        Assert.False(Directory.Exists(Path.Combine(destination, "mockly")));
        Assert.True(Directory.Exists(Path.Combine(destination, "contoso.widgets", "2.3.0")));
        Assert.True(File.Exists(Path.Combine(destination, InstallManifest.FileName)));
    }

    [Fact]
    public void Uninstall_with_dryRun_writes_nothing()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);

        var removed = _installer.Uninstall(destination, packageId: null, packageVersion: null, dryRun: true);

        Assert.Single(removed);
        Assert.True(Directory.Exists(Path.Combine(destination, "mockly", "1.10.0")));
    }

    [Fact]
    public void Uninstall_on_an_untouched_folder_reports_nothing_to_do()
    {
        using var temp = new TempDirectory();

        Assert.Empty(_installer.Uninstall(temp.Combine("dest"), packageId: null, packageVersion: null, dryRun: false));
    }

    [Fact]
    public void Uninstall_leaves_hand_authored_skills_in_place()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);

        var handAuthored = Path.Combine(destination, "our-own-skill");
        Directory.CreateDirectory(handAuthored);
        File.WriteAllText(Path.Combine(handAuthored, "SKILL.md"), "ours");

        _installer.Uninstall(destination, packageId: null, packageVersion: null, dryRun: false);

        Assert.True(File.Exists(Path.Combine(handAuthored, "SKILL.md")));
    }

    [Fact]
    public void A_corrupt_manifest_does_not_block_installing()
    {
        using var temp = new TempDirectory();
        var destination = temp.CreateDirectory("dest");
        File.WriteAllText(Path.Combine(destination, InstallManifest.FileName), "{ not json");

        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);

        Assert.True(File.Exists(Path.Combine(destination, "mockly", "1.10.0", "SKILL.md")));
    }
}
