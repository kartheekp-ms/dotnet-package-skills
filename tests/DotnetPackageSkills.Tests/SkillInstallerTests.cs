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
        var contoso = Assert.Single(manifest.Installed, entry => entry.Package == "Contoso.Widgets");
        Assert.Equal(
            ["contoso.widgets-widget-testing", "contoso.widgets-widget-usage"],
            contoso.Skills);

        var json = File.ReadAllText(Path.Combine(destination, InstallManifest.FileName));
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
        Assert.Equal("1.11.0", Assert.Single(InstallManifest.Load(destination).Installed).Version);
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

        Assert.False(Directory.Exists(Path.Combine(destination, "widget-usage")));
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
    public void Pruning_the_last_tracked_skill_removes_the_manifest_like_uninstall_does()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);

        _installer.Install(destination, [], dryRun: false);

        Assert.False(File.Exists(Path.Combine(destination, InstallManifest.FileName)));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void Pruning_everything_still_keeps_a_folder_holding_hand_authored_skills()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);

        var handAuthored = temp.CreateFile("dest/our-own-skill/SKILL.md", "ours");

        _installer.Install(destination, [], dryRun: false);

        Assert.False(File.Exists(Path.Combine(destination, InstallManifest.FileName)));
        Assert.Equal("ours", File.ReadAllText(handAuthored));
    }

    [Fact]
    public void Install_never_touches_skills_it_did_not_install()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        // Pruning is driven by the manifest, so a hand-authored skill sitting alongside
        // package-provided ones has to survive every install.
        Directory.CreateDirectory(destination);
        var handAuthored = Path.Combine(destination, "our-own-skill");
        Directory.CreateDirectory(handAuthored);
        File.WriteAllText(Path.Combine(handAuthored, "SKILL.md"), "ours");

        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);
        _installer.Install(destination, [], dryRun: false);

        Assert.True(File.Exists(Path.Combine(handAuthored, "SKILL.md")));
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
            prune: false);

        Assert.Empty(outcome.Installed);
        Assert.Single(outcome.Skipped);
        Assert.Equal("Contoso.Widgets", Assert.Single(InstallManifest.Load(destination).Installed).Package);
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
        Assert.Contains("Contoso.Widgets", Assert.Single(outcome.Skipped).Reason);
        Assert.Equal("Contoso.Widgets", Assert.Single(InstallManifest.Load(destination).Installed).Package);
        Assert.Equal(contents, File.ReadAllBytes(Path.Combine(destination, "shared-skill", "SKILL.md")));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_conflicting_copy_cannot_delete_its_current_owner_through_deselection(bool prune)
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Alpha", "1.0.0", "shared")], dryRun: false);

        var result = _installer.Install(
            destination, [Skill(temp, "Beta", "2.0.0", "shared")],
            dryRun: false, prune: prune, deselected: ["shared"]);

        Assert.Empty(result.Installed);
        Assert.Empty(result.Removed);
        Assert.Single(result.Skipped);
        Assert.Equal("Alpha", Assert.Single(InstallManifest.Load(destination).Installed).Package);
        Assert.True(File.Exists(Path.Combine(destination, "shared", "SKILL.md")));
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
                _installer.Install(destination, [], false, false, ["shared"], observed);
            }
        });

        Assert.Contains("ownership changed", error.Message);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName)));
        Assert.True(File.Exists(Path.Combine(destination, "shared", "SKILL.md")));
    }

    [Fact]
    public void Same_package_upgrades_remain_allowed_in_an_additive_selection()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Alpha", "1.0.0", "shared")], dryRun: false);

        var result = _installer.Install(
            destination, [Skill(temp, "Alpha", "2.0.0", "shared")], dryRun: false, prune: false);

        Assert.Empty(result.Skipped);
        Assert.Single(result.Installed);
        Assert.Equal("2.0.0", Assert.Single(InstallManifest.Load(destination).Installed).Version);
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
    public void Pruning_reports_removed_tracking_entries_even_when_the_folder_was_already_deleted()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Alpha", "1.0.0", "gone")], dryRun: false);
        Directory.Delete(Path.Combine(destination, "gone"), recursive: true);

        var preview = _installer.Install(destination, [], dryRun: true);
        var applied = _installer.Install(destination, [], dryRun: false);

        Assert.Equal(preview.Removed, applied.Removed);
        Assert.Equal("gone", Assert.Single(applied.Removed).Skill);
        Assert.False(File.Exists(Path.Combine(destination, InstallManifest.FileName)));
    }

    [Fact]
    public void Install_removes_a_deselected_skill_even_when_the_install_is_additive()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [Skill(temp, "Mockly", "1.10.0", "mockly"), Skill(temp, "Contoso.Widgets", "2.3.0", "widget-usage")],
            dryRun: false);

        // Additive normally means "this says nothing about the rest", but a deselection is an
        // instruction rather than an inference, so it removes regardless.
        var outcome = _installer.Install(
            destination,
            [Skill(temp, "Mockly", "1.10.0", "mockly")],
            dryRun: false,
            prune: false,
            deselected: ["widget-usage"]);

        Assert.Equal("widget-usage", Assert.Single(outcome.Removed).Skill);
        Assert.False(Directory.Exists(Path.Combine(destination, "widget-usage")));
        Assert.Equal("Mockly", Assert.Single(InstallManifest.Load(destination).Installed).Package);
    }

    [Fact]
    public void Install_still_leaves_additive_skills_nobody_deselected()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(
            destination,
            [Skill(temp, "Mockly", "1.10.0", "mockly"), Skill(temp, "Contoso.Widgets", "2.3.0", "widget-usage")],
            dryRun: false);

        _installer.Install(
            destination,
            [Skill(temp, "Mockly", "1.10.0", "mockly")],
            dryRun: false,
            prune: false,
            deselected: []);

        Assert.True(Directory.Exists(Path.Combine(destination, "widget-usage")));
    }

    [Fact]
    public void Install_keeps_a_skill_that_is_both_selected_and_deselected()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        var skill = Skill(temp, "Mockly", "1.10.0", "mockly");
        _installer.Install(destination, [skill], dryRun: false);

        var outcome = _installer.Install(
            destination,
            [skill],
            dryRun: false,
            prune: false,
            deselected: ["mockly"]);

        Assert.Empty(outcome.Removed);
        Assert.True(File.Exists(Path.Combine(destination, "mockly", "SKILL.md")));
    }

    [Fact]
    public void Install_with_dryRun_reports_a_deselected_skill_without_removing_it()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        _installer.Install(destination, [Skill(temp, "Mockly", "1.10.0", "mockly")], dryRun: false);

        var outcome = _installer.Install(
            destination,
            [],
            dryRun: true,
            prune: false,
            deselected: ["mockly"]);

        Assert.Equal("mockly", Assert.Single(outcome.Removed).Skill);
        Assert.True(Directory.Exists(Path.Combine(destination, "mockly")));
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
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"note":"missing ownership data"}""")]
    [InlineData("""{"installed":[],"Installed":[]}""")]
    [InlineData("""{"installed":null}""")]
    [InlineData("""{"installed":[{"package":null,"version":"1.0.0","skills":["mockly"]}]}""")]
    [InlineData("""{"installed":[{"package":"Mockly","version":"1.0.0","skills":[null]}]}""")]
    [InlineData("""{"installed":[{"package":"Mockly","version":"1.0.0","skills":["../outside"]}]}""")]
    [InlineData("""{"installed":[{"package":"Alpha","version":"1.0.0","skills":["shared"]},{"package":"Beta","version":"1.0.0","skills":["SHARED"]}]}""")]
    [InlineData("""{"installed":[{"package":"Alpha","version":"1.0.0","skills":["shared","shared"]}]}""")]
    public void A_manifest_with_an_unusable_shape_fails_instead_of_crashing_later(string contents)
    {
        using var temp = new TempDirectory();
        var destination = temp.CreateDirectory("dest");
        var manifest = Path.Combine(destination, InstallManifest.FileName);
        File.WriteAllText(manifest, contents);

        var error = Assert.Throws<PackageSkillsException>(() => InstallManifest.Load(destination));

        Assert.Contains("could not read", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(contents, File.ReadAllText(manifest));
    }
}
