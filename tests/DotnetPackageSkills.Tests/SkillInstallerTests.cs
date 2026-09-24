using DotnetPackageSkills.NuGet;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class SkillInstallerTests
{
    private readonly SkillInstaller _installer = new();

    private static BundledSkill Skill(
        TempDirectory temp,
        string packageId,
        string version,
        string skillName)
    {
        var packageDirectory = temp.CreatePackageWithSkill(packageId, version, skillName);

        return new BundledSkill(
            packageId,
            version,
            skillName,
            Path.Combine(packageDirectory, "skills", skillName),
            skillName);
    }

    [Fact]
    public void Install_copies_a_skill_to_its_authored_folder_name()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);

        Assert.True(File.Exists(Path.Combine(destination, "mockly", "SKILL.md")));
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
            File.ReadAllText(temp.Combine("dest", "widget-usage", "references", "batching.md")));
    }

    [Fact]
    public void Manifest_groups_skill_names_by_package_and_version()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        _installer.Install(
            destination,
            [
                Skill(temp, "Contoso.Widgets", "2.3.0", "contoso.widgets-widget-testing"),
                Skill(temp, "Contoso.Widgets", "2.3.0", "contoso.widgets-widget-usage"),
                Skill(temp, "Mockly", "1.10.0", "mockly"),
            ],
            dryRun: false);

        var manifest = InstallManifest.Load(destination);
        Assert.Equal(
            ["contoso.widgets-widget-testing", "contoso.widgets-widget-usage"],
            manifest.Packages["contoso.widgets"].Skills);

        var json = File.ReadAllText(Path.Combine(destination, InstallManifest.FileName));
        Assert.Contains("\"packages\":", json);
        Assert.Contains("\"skills\":", json);
        Assert.DoesNotContain("\"path\":", json);
        Assert.DoesNotContain("\"skill\":", json);
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

        Assert.True(Directory.Exists(Path.Combine(destination, "mockly")));
        Assert.Empty(outcome.Removed);
        Assert.Equal("1.11.0", Assert.Single(InstallManifest.Load(destination).Packages).Value.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_upgrade_removes_the_skills_the_new_version_no_longer_ships(bool dryRun)
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [Skill(temp, "Mockly", "1.10.0", "mockly-usage"), Skill(temp, "Mockly", "1.10.0", "mockly-migration")],
            dryRun: false);
        var before = Snapshot(destination);

        var outcome = _installer.Install(destination, [Skill(temp, "Mockly", "1.11.0", "mockly-usage")], dryRun);

        Assert.Equal("mockly-usage", Assert.Single(outcome.Installed).SkillName);
        Assert.Equal(new TrackedSkill("mockly", "1.10.0", "mockly-migration"), Assert.Single(outcome.Removed));
        if (dryRun)
        {
            Assert.Equal(before, Snapshot(destination));
        }
        else
        {
            Assert.False(Directory.Exists(Path.Combine(destination, "mockly-migration")));
            var package = Assert.Single(InstallManifest.Load(destination).Packages);
            Assert.Equal("1.11.0", package.Value.Version);
            Assert.Equal(["mockly-usage"], package.Value.Skills);
        }
    }

    [Fact]
    public void An_upgrade_to_a_version_without_skills_removes_every_skill_of_that_package()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [Skill(temp, "Mockly", "1.10.0", "mockly-usage"), Skill(temp, "Contoso.Widgets", "2.3.0", "widget-usage")],
            dryRun: false);

        var outcome = _installer.Install(destination, [], dryRun: false, offered: Offer(("Mockly", "1.11.0")));

        Assert.Equal("mockly-usage", Assert.Single(outcome.Removed).Skill);
        Assert.False(Directory.Exists(Path.Combine(destination, "mockly-usage")));
        Assert.Equal("contoso.widgets", Assert.Single(InstallManifest.Load(destination).Packages).Key);
    }

    [Fact]
    public void The_same_version_never_removes_a_tracked_skill_it_does_not_ship()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [Skill(temp, "Mockly", "1.10.0", "mockly-usage"), Skill(temp, "Mockly", "1.10.0", "mockly-migration")],
            dryRun: false);

        // A package version never changes, so a skill missing from it means the cache is not
        // what it was. Guessing that the author removed the skill would delete it on a hunch.
        var outcome = _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly-usage")], dryRun: false);

        Assert.Empty(outcome.Removed);
        Assert.True(File.Exists(Path.Combine(destination, "mockly-migration", "SKILL.md")));
        Assert.Equal(
            ["mockly-migration", "mockly-usage"],
            InstallManifest.Load(destination).Packages["mockly"].Skills);
    }

    [Fact]
    public void Skills_of_packages_the_run_does_not_offer_are_kept_and_reported_as_untouched()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [Skill(temp, "Mockly", "1.10.0", "mockly"), Skill(temp, "Contoso.Widgets", "2.3.0", "widget-usage")],
            dryRun: false);

        var outcome = _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);

        Assert.Empty(outcome.Removed);
        Assert.Equal(new TrackedSkill("contoso.widgets", "2.3.0", "widget-usage"), Assert.Single(outcome.Untouched));
        Assert.True(File.Exists(Path.Combine(destination, "widget-usage", "SKILL.md")));
        Assert.Equal(["contoso.widgets", "mockly"], InstallManifest.Load(destination).Packages.Keys);
    }

    [Fact]
    public void Install_keeps_the_first_skill_when_destination_names_collide()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        var outcome = _installer.Install(
            destination,
            [
                Skill(temp, "Mockly", "1.10.0", "shared-skill"),
                Skill(temp, "Contoso.Widgets", "2.3.0", "shared-skill"),
            ],
            dryRun: false);

        Assert.Equal("Mockly", Assert.Single(outcome.Installed).PackageId);
        Assert.Equal("Contoso.Widgets", Assert.Single(outcome.Skipped).PackageId);
        Assert.True(File.Exists(Path.Combine(destination, "shared-skill", "SKILL.md")));
    }

    [Fact]
    public void Install_detects_destination_collisions_case_insensitively()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        var outcome = _installer.Install(
            destination,
            [
                Skill(temp, "Mockly", "1.10.0", "shared-skill"),
                Skill(temp, "Contoso.Widgets", "2.3.0", "SHARED-SKILL"),
            ],
            dryRun: false);

        Assert.Single(outcome.Installed);
        Assert.Single(outcome.Skipped);
    }

    [Fact]
    public void Install_with_nothing_to_record_leaves_no_folder_behind()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        // Most packages ship no skills, so this is the common outcome. It should not leave an
        // empty skills folder in a repository that never had one.
        _installer.Install(destination, [], dryRun: false);

        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void Removing_the_last_tracked_skill_on_an_upgrade_removes_the_manifest_like_uninstall_does()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);

        _installer.Install(destination, [], dryRun: false, offered: Offer(("Mockly", "1.11.0")));

        Assert.False(File.Exists(Path.Combine(destination, InstallManifest.FileName)));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void Removing_everything_still_keeps_a_folder_holding_hand_authored_skills()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);

        var handAuthored = temp.CreateFile("dest/our-own-skill/SKILL.md", "ours");

        _installer.Install(destination, [], dryRun: false, offered: Offer(("Mockly", "1.11.0")));

        Assert.False(File.Exists(Path.Combine(destination, InstallManifest.FileName)));
        Assert.Equal("ours", File.ReadAllText(handAuthored));
    }

    [Fact]
    public void Install_never_touches_skills_it_did_not_install()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        // Removal is driven by the manifest, so a hand-authored skill sitting alongside
        // package-provided ones has to survive every install.
        Directory.CreateDirectory(destination);
        var handAuthored = Path.Combine(destination, "our-own-skill");
        Directory.CreateDirectory(handAuthored);
        File.WriteAllText(Path.Combine(handAuthored, "SKILL.md"), "ours");

        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);
        _installer.Install(destination, [], dryRun: false, offered: Offer(("Mockly", "2.0.0")));

        Assert.True(File.Exists(Path.Combine(handAuthored, "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(destination, "mockly")));
    }

    [Fact]
    public void Install_skips_an_existing_untracked_destination_folder()
    {
        using var temp = new TempDirectory();
        var destination = temp.CreateDirectory("dest");
        var existing = temp.CreateFile("dest/mockly/SKILL.md", "ours");

        var outcome = _installer.Install(
            destination,
            [Skill(temp, "Mockly", "1.10.0", "mockly")],
            dryRun: false);

        Assert.Empty(outcome.Installed);
        Assert.Single(outcome.Skipped);
        Assert.Equal("ours", File.ReadAllText(existing));
    }

    [Fact]
    public void Install_skips_an_existing_file_at_the_destination_path()
    {
        using var temp = new TempDirectory();
        var destination = temp.CreateDirectory("dest");
        var existing = temp.CreateFile("dest/mockly", "ours");

        var outcome = _installer.Install(
            destination,
            [Skill(temp, "Mockly", "1.10.0", "mockly")],
            dryRun: false);

        Assert.Empty(outcome.Installed);
        Assert.Single(outcome.Skipped);
        Assert.Equal("ours", File.ReadAllText(existing));
    }

    [Fact]
    public void Additive_install_skips_a_path_tracked_for_another_package()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [Skill(temp, "Contoso.Widgets", "2.3.0", "shared-skill")],
            dryRun: false);

        var outcome = _installer.Install(
            destination,
            [Skill(temp, "Mockly", "1.10.0", "shared-skill")],
            dryRun: false,
            offered: Offer());

        Assert.Empty(outcome.Installed);
        Assert.Single(outcome.Skipped);
        Assert.Equal("contoso.widgets", Assert.Single(InstallManifest.Load(destination).Packages).Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Complete_install_preserves_a_path_owned_by_another_package(bool dryRun)
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [Skill(temp, "Contoso.Widgets", "2.3.0", "shared-skill")],
            dryRun: false);
        var contents = File.ReadAllBytes(Path.Combine(destination, "shared-skill", "SKILL.md"));
        var manifest = File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName));

        var outcome = _installer.Install(
            destination,
            [Skill(temp, "Mockly", "1.10.0", "shared-skill")],
            dryRun);

        Assert.Empty(outcome.Installed);
        Assert.Empty(outcome.Removed);
        Assert.Contains("managed for contoso.widgets", Assert.Single(outcome.Skipped).Reason);
        Assert.Equal("contoso.widgets", Assert.Single(InstallManifest.Load(destination).Packages).Key);
        Assert.Equal(contents, File.ReadAllBytes(Path.Combine(destination, "shared-skill", "SKILL.md")));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Moving_a_package_to_a_version_without_a_skill_that_another_package_ships_stops_before_any_change(
        bool dryRun)
    {
        // Removing the old copy would hand its name to the other package, which takes an explicit
        // uninstall. Keeping it would record the old version's copy under the new version, where
        // no later run would ever remove it.
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [Skill(temp, "Alpha", "1.0.0", "alpha-usage"), Skill(temp, "Alpha", "1.0.0", "shared")],
            dryRun: false);
        var before = Snapshot(destination);

        var error = Assert.Throws<PackageSkillsException>(() => _installer.Install(
            destination,
            [Skill(temp, "Alpha", "2.0.0", "alpha-usage"), Skill(temp, "Beta", "2.0.0", "shared")],
            dryRun,
            offered: Offer(("Alpha", "2.0.0"), ("Beta", "2.0.0"))));

        Assert.Contains(
            "Alpha 2.0.0 no longer ships the installed skill 'shared', and Beta 2.0.0 ships a skill with that name",
            error.Message);
        Assert.Contains("'dotnet-package-skills uninstall --package Alpha' first", error.Message);
        Assert.Contains("No skills were changed", error.Message);
        Assert.Equal(before, Snapshot(destination));
    }

    [Fact]
    public void A_conflicting_copy_keeps_its_owner_when_the_owners_package_is_not_part_of_the_run()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Alpha", "1.0.0", "shared")], dryRun: false);

        var result = _installer.Install(
            destination,
            [Skill(temp, "Beta", "2.0.0", "shared")],
            dryRun: false,
            offered: Offer(("Beta", "2.0.0")));

        Assert.Empty(result.Installed);
        Assert.Empty(result.Removed);
        Assert.Contains("managed for alpha 1.0.0", Assert.Single(result.Skipped).Reason);
        Assert.Equal(new TrackedSkill("alpha", "1.0.0", "shared"), Assert.Single(result.Untouched));
        var alpha = InstallManifest.Load(destination).Packages["alpha"];
        Assert.Equal("1.0.0", alpha.Version);
        Assert.Equal(["shared"], alpha.Skills);
    }

    [Fact]
    public void A_package_id_with_letters_outside_ascii_is_recorded_in_a_manifest_that_reads_back()
    {
        // NuGet accepts any Unicode letter in a package ID. A manifest that the tool's own reader
        // refused would stop every later install and uninstall with advice that can't help.
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        _installer.Install(destination, [Skill(temp, "Contoso.Überlib", "1.0.0", "uber-usage")], dryRun: false);

        Assert.Equal("contoso.überlib", Assert.Single(InstallManifest.Load(destination).Packages).Key);
        Assert.Single(_installer.Uninstall(destination, "Contoso.Überlib", null, dryRun: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_changed_owner_invalidates_an_interactive_selection_before_writing(bool uninstall)
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Alpha", "1.0.0", "shared")], dryRun: false);
        var observed = InstallManifest.Load(destination).EnumerateSkills().ToList();
        _installer.Uninstall(destination, null, null, dryRun: false);
        _installer.Install(destination, [Skill(temp, "Beta", "2.0.0", "shared")], dryRun: false);
        var before = File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName));

        var error = Assert.Throws<PackageSkillsException>(() =>
        {
            if (uninstall)
            {
                _installer.Uninstall(destination, null, null, false, ["shared"], observed);
            }
            else
            {
                _installer.Install(destination, [], dryRun: false, offered: Offer(), expectedInstalled: observed);
            }
        });

        Assert.Contains("ownership changed", error.Message);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName)));
        Assert.True(File.Exists(Path.Combine(destination, "shared", "SKILL.md")));
    }

    [Fact]
    public void Upgrading_one_package_leaves_the_other_packages_untouched()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [Skill(temp, "Alpha", "1.0.0", "shared"), Skill(temp, "Beta", "1.0.0", "beta")],
            dryRun: false);

        var result = _installer.Install(destination, [Skill(temp, "Alpha", "2.0.0", "shared")], dryRun: false);

        Assert.Empty(result.Skipped);
        Assert.Single(result.Installed);
        var manifest = InstallManifest.Load(destination);
        Assert.Equal("2.0.0", manifest.Packages["alpha"].Version);
        Assert.Equal("1.0.0", manifest.Packages["beta"].Version);
    }

    [Fact]
    public void An_only_adding_install_cannot_record_a_second_version_of_a_package()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly-usage")], dryRun: false);
        var before = Snapshot(destination);

        var error = Assert.Throws<PackageSkillsException>(() => _installer.Install(
            destination, [Skill(temp, "Mockly", "1.11.0", "mockly-testing")], dryRun: false, offered: Offer()));

        Assert.Contains("only one version", error.Message);
        Assert.Equal(before, Snapshot(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_source_that_disappears_after_discovery_blocks_all_writes(bool dryRun)
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Old", "1.0.0", "old")], dryRun: false);
        var first = Skill(temp, "Alpha", "1.0.0", "first");
        var missing = Skill(temp, "Beta", "1.0.0", "missing");
        Directory.Delete(missing.SourcePath, recursive: true);
        var before = File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName));

        var error = Assert.Throws<PackageSkillsException>(() =>
            _installer.Install(destination, [first, missing], dryRun: dryRun));

        Assert.Contains("no longer available", error.Message);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName)));
        Assert.True(File.Exists(Path.Combine(destination, "old", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(destination, "first")));
    }

    [Fact]
    public void An_upgrade_reports_removed_tracking_entries_even_when_the_folder_was_already_deleted()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Alpha", "1.0.0", "gone")], dryRun: false);
        Directory.Delete(Path.Combine(destination, "gone"), recursive: true);

        var preview = _installer.Install(destination, [], dryRun: true, offered: Offer(("Alpha", "2.0.0")));
        var applied = _installer.Install(destination, [], dryRun: false, offered: Offer(("Alpha", "2.0.0")));

        Assert.Equal(preview.Removed, applied.Removed);
        Assert.Equal("gone", Assert.Single(applied.Removed).Skill);
        Assert.False(File.Exists(Path.Combine(destination, InstallManifest.FileName)));
    }

    [Fact]
    public void An_only_adding_install_leaves_every_other_tracked_skill_alone()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [Skill(temp, "Mockly", "1.10.0", "mockly"), Skill(temp, "Contoso.Widgets", "2.3.0", "widget-usage")],
            dryRun: false);
        var installedCopy = temp.CreateFile("dest/mockly/local-notes.md", "edited after install");

        var outcome = _installer.Install(
            destination,
            [Skill(temp, "Gamma", "1.0.0", "gamma")],
            dryRun: false,
            offered: Offer());

        Assert.Equal("gamma", Assert.Single(outcome.Installed).SkillName);
        Assert.Empty(outcome.Removed);
        Assert.Equal("edited after install", File.ReadAllText(installedCopy));
        Assert.True(Directory.Exists(Path.Combine(destination, "widget-usage")));
        Assert.Equal(
            ["contoso.widgets", "gamma", "mockly"],
            InstallManifest.Load(destination).Packages.Keys);
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

        Assert.False(File.Exists(Path.Combine(destination, "mockly", "obsolete.md")));
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

            var copied = Path.Combine(destination, "mockly", "SKILL.md");
            Assert.False(File.GetAttributes(copied).HasFlag(FileAttributes.ReadOnly));

            // The real point: a second install must be able to overwrite the copy.
            _installer.Install(destination, [skill], dryRun: false);
        }
        finally
        {
            File.SetAttributes(source, File.GetAttributes(source) & ~FileAttributes.ReadOnly);
        }
    }

    [Fact]
    public void Uninstall_against_references_removes_only_stale_skills_within_a_chosen_list()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [
                Skill(temp, "Mockly", "1.10.0", "mockly-usage"),
                Skill(temp, "Mockly", "1.10.0", "mockly-setup"),
                Skill(temp, "Contoso.Widgets", "2.3.0", "widget-usage"),
            ],
            dryRun: false);
        PackageReferenceInfo[] references = [new("Contoso.Widgets", "2.3.0"), new("Mockly", "1.11.0")];

        var removed = _installer.Uninstall(
            destination, packageId: null, packageVersion: null, dryRun: false,
            only: ["mockly-setup", "widget-usage"], staleAgainst: references);

        // widget-usage was chosen, but the target still references its version.
        Assert.Equal("mockly-setup", Assert.Single(removed).Skill);
        Assert.True(Directory.Exists(Path.Combine(destination, "mockly-usage")));
        Assert.True(Directory.Exists(Path.Combine(destination, "widget-usage")));
    }

    [Fact]
    public void Uninstall_removes_only_the_skills_it_was_given()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [
                Skill(temp, "Mockly", "1.10.0", "mockly-usage"),
                Skill(temp, "Mockly", "1.10.0", "mockly-setup"),
                Skill(temp, "Contoso.Widgets", "2.3.0", "widget-usage"),
            ],
            dryRun: false);

        // What the interactive picker hands back: an explicit list, not a package filter.
        var removed = _installer.Uninstall(
            destination,
            packageId: null,
            packageVersion: null,
            dryRun: false,
            only: ["mockly-setup", "widget-usage"]);

        Assert.Equal(["mockly-setup", "widget-usage"], removed.Select(entry => entry.Skill));
        Assert.True(Directory.Exists(Path.Combine(destination, "mockly-usage")));
        Assert.False(Directory.Exists(Path.Combine(destination, "mockly-setup")));
        Assert.Single(InstallManifest.Load(destination).EnumerateSkills());
    }

    [Fact]
    public void Uninstall_given_an_empty_list_removes_nothing()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);

        // Confirming the picker without ticking anything must not be read as "all of them".
        var removed = _installer.Uninstall(
            destination,
            packageId: null,
            packageVersion: null,
            dryRun: false,
            only: []);

        Assert.Empty(removed);
        Assert.True(Directory.Exists(Path.Combine(destination, "mockly")));
    }

    [Fact]
    public void Uninstall_combines_a_chosen_list_with_a_package_filter()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [
                Skill(temp, "Mockly", "1.10.0", "mockly-usage"),
                Skill(temp, "Contoso.Widgets", "2.3.0", "widget-usage"),
            ],
            dryRun: false);

        var removed = _installer.Uninstall(
            destination,
            packageId: "Mockly",
            packageVersion: null,
            dryRun: false,
            only: ["mockly-usage", "widget-usage"]);

        // widget-usage was ticked but belongs to another package, so the filter still holds.
        Assert.Equal("mockly-usage", Assert.Single(removed).Skill);
        Assert.True(Directory.Exists(Path.Combine(destination, "widget-usage")));
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
        Assert.True(Directory.Exists(Path.Combine(destination, "widget-usage")));
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
        Assert.True(Directory.Exists(Path.Combine(destination, "mockly")));
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
    public void A_corrupt_manifest_blocks_install_and_preserves_everything()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [Skill(temp, "Contoso.Widgets", "2.3.0", "already-installed")],
            dryRun: false);

        var manifest = Path.Combine(destination, InstallManifest.FileName);
        const string corrupt = "{ not json";
        File.WriteAllText(manifest, corrupt);

        var error = Assert.Throws<PackageSkillsException>(() =>
            _installer.Install(
                destination,
                [Skill(temp, "Mockly", "1.10.0", "not-installed")],
                dryRun: false));

        Assert.Contains(manifest, error.Message);
        Assert.Contains("preserved", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(corrupt, File.ReadAllText(manifest));
        Assert.True(File.Exists(Path.Combine(destination, "already-installed", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(destination, "not-installed")));
    }

    [Fact]
    public void A_corrupt_manifest_blocks_uninstall_and_preserves_everything()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [Skill(temp, "Mockly", "1.10.0", "mockly")],
            dryRun: false);

        var manifest = Path.Combine(destination, InstallManifest.FileName);
        const string corrupt = """
            <<<<<<< HEAD
            {"installed":[]}
            =======
            {"installed":[]}
            >>>>>>> feature
            """;
        File.WriteAllText(manifest, corrupt);

        var error = Assert.Throws<PackageSkillsException>(() =>
            _installer.Uninstall(
                destination,
                packageId: null,
                packageVersion: null,
                dryRun: false));

        Assert.Contains(manifest, error.Message);
        Assert.Equal(corrupt, File.ReadAllText(manifest));
        Assert.True(File.Exists(Path.Combine(destination, "mockly", "SKILL.md")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Unsafe_manifest_aliases_block_all_changes_and_preserve_handwritten_skills(bool uninstall, bool dryRun)
    {
        foreach (var unsafeName in new[] { "...", "....", ".. ", "... ", "our-own-skill.", "our-own-skill " })
        {
            using var temp = new TempDirectory();
            var destination = temp.Combine("dest");
            _installer.Install(
                destination,
                [Skill(temp, "Contoso.Widgets", "2.3.0", "already-installed")],
                dryRun: false);
            temp.CreateFile("dest/our-own-skill/SKILL.md", "handwritten guidance");
            temp.CreateFile("dest/our-own-skill/references/details.md", "handwritten reference");
            var next = Skill(temp, "Mockly", "1.10.0", "not-installed");
            var manifest = Path.Combine(destination, InstallManifest.FileName);
            File.WriteAllText(manifest, $$"""
                {
                  "version": 1,
                  "packages": {
                    "contoso.widgets": {"version":"2.3.0","skills":["already-installed"]},
                    "mockly": {"version":"1.10.0","skills":["{{unsafeName}}"]}
                  }
                }
                """);
            var before = Snapshot(destination);

            var error = Assert.Throws<PackageSkillsException>(() =>
            {
                if (uninstall)
                {
                    _installer.Uninstall(destination, packageId: null, packageVersion: null, dryRun);
                }
                else
                {
                    _installer.Install(destination, [next], dryRun);
                }
            });

            Assert.Contains("not a safe skill folder name", error.Message);
            Assert.Contains("No skills were changed", error.Message);
            Assert.Contains("preserved", error.Message);
            Assert.Equal(before, Snapshot(destination));
            Assert.False(Directory.Exists(Path.Combine(destination, "not-installed")));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unsafe_candidate_paths_cannot_bypass_manifest_validation_or_prune_existing_skills(bool dryRun)
    {
        foreach (var unsafeName in new[] { "...", "..", "our-own-skill.", "our-own-skill ", "../outside" })
        {
            using var temp = new TempDirectory();
            var destination = temp.Combine("dest");
            _installer.Install(destination, [Skill(temp, "Old", "1.0.0", "old")], dryRun: false);
            temp.CreateFile("dest/our-own-skill/SKILL.md", "handwritten guidance");
            temp.CreateFile("outside/SKILL.md", "outside the destination");
            var next = Skill(temp, "Alpha", "1.0.0", "next");
            var unsafeSkill = Skill(temp, "Beta", "1.0.0", "source") with
            {
                SkillName = unsafeName,
                RelativePath = unsafeName,
            };
            var before = Snapshot(temp.Path);

            var error = Assert.Throws<PackageSkillsException>(() =>
                _installer.Install(destination, [next, unsafeSkill], dryRun));

            Assert.Contains("safe skill folder", error.Message);
            Assert.Equal(before, Snapshot(temp.Path));
        }
    }

    [Theory]
    [InlineData(".hidden-skill")]
    [InlineData("...usage")]
    [InlineData("skill name")]
    public void Safe_names_with_dots_or_spaces_remain_installable_and_removable(string skillName)
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest") + Path.DirectorySeparatorChar;
        var handwritten = temp.CreateFile("dest/our-own-skill/SKILL.md", "ours");

        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", skillName)], dryRun: false);
        Assert.True(File.Exists(Path.Combine(destination, skillName, "SKILL.md")));

        var removed = _installer.Uninstall(destination, packageId: null, packageVersion: null, dryRun: false);

        Assert.Equal(skillName, Assert.Single(removed).Skill);
        Assert.False(Directory.Exists(Path.Combine(destination, skillName)));
        Assert.False(File.Exists(Path.Combine(destination, InstallManifest.FileName)));
        Assert.Equal("ours", File.ReadAllText(handwritten));
    }

    private static (string Path, string Contents)[] Snapshot(string root) =>
        Directory.Exists(root)
            ?
            [
                .. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .Select(path => (Path.GetRelativePath(root, path), Convert.ToHexString(File.ReadAllBytes(path)))),
            ]
            : [];

    private static Dictionary<string, string> Offer(params (string Id, string Version)[] packages) =>
        packages.ToDictionary(package => package.Id, package => package.Version, StringComparer.OrdinalIgnoreCase);
}
