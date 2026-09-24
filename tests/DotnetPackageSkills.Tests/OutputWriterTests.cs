using System.Text.Json;
using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class OutputWriterTests
{
    private const string ClipboardControl = "\u001b]52;c;ZWNobyBleGFtcGxl\u0007";

    [Fact]
    public void The_trust_notice_is_a_single_line()
    {
        using var output = new StringWriter();

        new OutputWriter(output).WriteInstallReport(ResultWithCollision(), copied: true);

        // Any break we pick is a guess at the reader's width. It is one thought, so it goes
        // out as one line and the terminal wraps it wherever it needs to.
        var notice = output.ToString()
            .Split(Environment.NewLine)
            .Single(line => line.StartsWith("These skills are", StringComparison.Ordinal));

        Assert.EndsWith("Review them before relying on them.", notice);
    }

    [Fact]
    public void No_reported_line_breaks_in_the_middle_of_a_sentence()
    {
        using var output = new StringWriter();

        new OutputWriter(output).WriteInstallReport(ResultWithCollision(), copied: true);

        // A line that ends without terminal punctuation, followed by one starting lower
        // case, is prose someone hard-wrapped. Indented lines are data, not prose.
        var lines = output.ToString()
            .Split(Environment.NewLine)
            .Where(line => line.Length > 0 && !line.StartsWith(' '))
            .ToList();

        for (var index = 0; index < lines.Count - 1; index++)
        {
            var ends = lines[index].TrimEnd();
            var next = lines[index + 1];

            Assert.False(
                ends.Length > 0 && ends[^1] is not ('.' or ':' or '!' or '?') && char.IsLower(next[0]),
                $"'{ends}' looks hard-wrapped into '{next}'");
        }
    }

    [Fact]
    public void Install_report_warns_about_skipped_collisions()
    {
        using var output = new StringWriter();
        var result = ResultWithCollision();

        new OutputWriter(output).WriteInstallReport(result, copied: true);

        var report = output.ToString();
        Assert.Contains("Warning: skipped 1 colliding skill:", report);
        Assert.Contains("shared-skill (Beta.Widgets 2.0.0)", report);
        Assert.Contains("selected first", report);
    }

    [Fact]
    public void Skills_whose_package_left_the_target_are_listed_with_the_command_that_removes_them()
    {
        using var output = new StringWriter();
        var result = ResultWithCollision() with
        {
            Unreferenced = [new TrackedSkill("contoso.widgets", "2.3.0", "contoso.widgets-usage")],
        };

        new OutputWriter(output).WriteInstallReport(result, copied: true);

        Assert.Contains(
            "1 installed skill belongs to a package that the target no longer references:" + Environment.NewLine +
            "  contoso.widgets-usage (contoso.widgets 2.3.0)" + Environment.NewLine +
            "Run 'dotnet package-skills uninstall --stale' to remove it.",
            output.ToString());
    }

    [Theory]
    [InlineData("other.package", "packages")]
    [InlineData("contoso.widgets", "a package")]
    public void The_stale_hint_counts_skills_and_packages_separately(string secondPackage, string packages)
    {
        using var output = new StringWriter();
        var result = ResultWithCollision() with
        {
            Unreferenced =
            [
                new TrackedSkill("contoso.widgets", "2.3.0", "contoso.widgets-usage"),
                new TrackedSkill(secondPackage, "2.3.0", "second-skill"),
            ],
        };

        new OutputWriter(output).WriteInstallReport(result, copied: true);

        Assert.Contains($"2 installed skills belong to {packages} that the target no longer references:", output.ToString());
        Assert.Contains("Run 'dotnet package-skills uninstall --stale' to remove them.", output.ToString());
    }

    [Fact]
    public void The_stale_hint_prints_the_command_for_the_destination_and_target_that_were_used()
    {
        using var output = new StringWriter();
        var result = ResultWithCollision() with
        {
            Unreferenced = [new TrackedSkill("contoso.widgets", "2.3.0", "contoso.widgets-usage")],
            StaleCommand = "dotnet package-skills uninstall --stale --destination \"my\u001b[2Jskills\"",
        };

        new OutputWriter(output).WriteInstallReport(result, copied: true);

        Assert.Contains(
            "Run 'dotnet package-skills uninstall --stale --destination \"myskills\"' to remove it.",
            output.ToString());
        Assert.DoesNotContain('\u001b', output.ToString());
    }

    [Fact]
    public void Deselecting_everything_does_not_claim_the_packages_ship_no_skills()
    {
        using var output = new StringWriter();
        var result = ResultWithCollision() with { Skills = [], SkillsDiscovered = 2 };

        new OutputWriter(output).WriteInstallReport(result, copied: true);

        var report = output.ToString();
        Assert.Contains("Copied no skills.", report);
        Assert.DoesNotContain("ship a skills/ folder", report);
    }

    [Fact]
    public void A_scan_that_discovered_nothing_says_so_plainly()
    {
        using var output = new StringWriter();
        var result = ResultWithCollision() with { Skills = [], Skipped = [], SkillsDiscovered = 0 };

        new OutputWriter(output).WriteInstallReport(result, copied: true);

        // A package missing from the cache is indistinguishable from one without skills, so the
        // report claims nothing about why nothing was found.
        Assert.Contains($"{Environment.NewLine}No bundled skills found.{Environment.NewLine}", output.ToString());
        Assert.DoesNotContain("ship a skills/ folder", output.ToString());
        Assert.DoesNotContain("not extracted", output.ToString());
    }

    [Theory]
    [InlineData(false, "Nothing new to install. Every skill that these packages ship is already installed.")]
    [InlineData(true, "Nothing new to install.")]
    public void An_interactive_install_with_nothing_to_offer_says_so(bool skipped, string expected)
    {
        using var output = new StringWriter();
        var result = ResultWithCollision() with
        {
            Skills = [],
            SkillsDiscovered = 1,
            NothingNewToInstall = true,
            Skipped = skipped ? ResultWithCollision().Skipped : [],
        };

        new OutputWriter(output).WriteInstallReport(result, copied: true);

        var lines = output.ToString().Split(Environment.NewLine);
        Assert.Contains(expected, lines);
        Assert.DoesNotContain("Copied no skills.", lines);
        Assert.Equal(skipped, output.ToString().Contains("Warning: skipped 1 colliding skill:", StringComparison.Ordinal));
    }

    [Fact]
    public void List_reports_what_it_found_rather_than_a_pending_copy()
    {
        using var output = new StringWriter();

        // list always runs as a dry run internally, but it is a query: it was never going
        // to copy anything, so "Would copy" would misdescribe it.
        new OutputWriter(output).WriteInstallReport(ResultWithCollision() with { DryRun = true }, copied: false);

        var report = output.ToString();
        Assert.Contains("Found 1 skill:", report);
        Assert.DoesNotContain("Would copy", report);
    }

    [Fact]
    public void An_install_dry_run_still_says_what_it_would_copy()
    {
        using var output = new StringWriter();

        new OutputWriter(output).WriteInstallReport(ResultWithCollision() with { DryRun = true }, copied: true);

        Assert.Contains("Would copy 1 skill:", output.ToString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Install_and_list_reports_sanitize_every_untrusted_display_field(bool copied, bool dryRun)
    {
        var clean = ResultWithCollision() with
        {
            DryRun = dryRun,
            Removed = [new TrackedSkill("Old.Package", "1.0.0", "old-skill")],
            Unreferenced = [new TrackedSkill("left.package", "3.0.0", "left-skill")],
        };
        var untrusted = clean with
        {
            Target = WithControls(clean.Target!),
            GlobalPackagesFolder = WithControls(clean.GlobalPackagesFolder),
            Destination = WithControls(clean.Destination),
            Skills =
            [
                .. clean.Skills.Select(skill => skill with
                {
                    SkillName = WithControls(skill.SkillName),
                    RelativePath = WithControls(skill.RelativePath),
                    PackageId = WithControls(skill.PackageId),
                    PackageVersion = WithControls(skill.PackageVersion),
                }),
            ],
            Removed =
            [
                .. clean.Removed.Select(skill => new TrackedSkill(
                    WithControls(skill.Package), WithControls(skill.Version), WithControls(skill.Skill))),
            ],
            Unreferenced =
            [
                .. clean.Unreferenced.Select(skill => new TrackedSkill(
                    WithControls(skill.Package), WithControls(skill.Version), WithControls(skill.Skill))),
            ],
            Skipped =
            [
                .. clean.Skipped.Select(skill => skill with
                {
                    SkillName = WithControls(skill.SkillName),
                    RelativePath = WithControls(skill.RelativePath),
                    PackageId = WithControls(skill.PackageId),
                    PackageVersion = WithControls(skill.PackageVersion),
                    Reason = WithControls(skill.Reason),
                }),
            ],
        };
        using var expected = new StringWriter();
        using var actual = new StringWriter();

        new OutputWriter(expected).WriteInstallReport(clean, copied);
        new OutputWriter(actual).WriteInstallReport(untrusted, copied);

        AssertPlainText(actual.ToString());
        Assert.Equal(expected.ToString(), actual.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Uninstall_reports_strip_clipboard_and_other_terminal_sequences(bool dryRun)
    {
        string[] controls =
        [
            ClipboardControl,
            "\u001b]52;c;ZWNobyBleGFtcGxl\u001b\\",
            "\u009d52;c;ZWNobyBleGFtcGxl\u009c",
            "\u001b[2J\u001b[H",
            "\u009b2J",
            "\u001b]8;;https://invalid.example\u001b\\",
            "\u001bPignored\u001b\\",
            "\u0007\u0008\u007f\u202e\u2066",
        ];
        using var expected = new StringWriter();
        new OutputWriter(expected).WriteUninstallReport(
            [new TrackedSkill("Example.Package", "1.0.0", "example-skill")], @"C:\repo", dryRun);

        foreach (var control in controls)
        {
            using var actual = new StringWriter();
            new OutputWriter(actual).WriteUninstallReport(
                [new TrackedSkill("Example" + control + ".Package", "1.0" + control + ".0",
                    "example" + control + "-skill")],
                @"C:\re" + control + "po", dryRun);

            AssertPlainText(actual.ToString());
            Assert.Equal(expected.ToString(), actual.ToString());
        }
    }

    [Theory]
    [InlineData(false, "Removed 1 skill:")]
    [InlineData(true, "Would remove 1 skill:")]
    public void A_stale_uninstall_report_names_the_target_it_compared_against(bool dryRun, string heading)
    {
        using var output = new StringWriter();

        new OutputWriter(output).WriteUninstallReport(
            [new TrackedSkill("contoso.widgets", "2.3.0", "widget-usage")], @"C:\repo\.agents\skills", dryRun,
            target: @"C:\repo\App.sln");

        Assert.Equal(
            [
                @"Target:      C:\repo\App.sln",
                @"Destination: C:\repo\.agents\skills",
                string.Empty,
                heading,
                "  widget-usage (contoso.widgets 2.3.0)",
                string.Empty,
            ],
            output.ToString().Split(Environment.NewLine));
    }

    [Fact]
    public void A_stale_uninstall_with_nothing_stale_says_so()
    {
        using var output = new StringWriter();

        new OutputWriter(output).WriteUninstallReport([], @"C:\repo\.agents\skills", dryRun: false, target: @"C:\repo\App.sln");

        Assert.Contains("Nothing to remove. No stale skills were found.", output.ToString());
        Assert.DoesNotContain("No skills installed by this tool", output.ToString());
    }

    [Fact]
    public void An_unterminated_control_in_one_identity_field_cannot_hide_the_following_fields()
    {
        using var output = new StringWriter();

        new OutputWriter(output).WriteUninstallReport(
            [new TrackedSkill("Example\u001b]52;c;unterminated", "1.0.0", "example-skill")],
            @"C:\repo", dryRun: true);

        AssertPlainText(output.ToString());
        Assert.Contains("example-skill (Example 1.0.0)", output.ToString());
    }

    [Fact]
    public void A_manifest_clipboard_payload_is_safe_to_preview_without_rewriting_identity_or_files()
    {
        using var temp = new TempDirectory();
        var destination = temp.CreateDirectory("dest");
        var skillFile = temp.CreateFile("dest/example-skill/SKILL.md", "installed guidance");
        var handwritten = temp.CreateFile("dest/our-own-skill/SKILL.md", "handwritten guidance");
        // Package ids are validated when the manifest is read, so the version carries the payload.
        var version = "1.0.0" + ClipboardControl;
        var manifest = temp.CreateFile("dest/.dotnet-package-skills.json", JsonSerializer.Serialize(new
        {
            version = 1,
            packages = new Dictionary<string, object>
            {
                ["example"] = new { version, skills = new[] { "example-skill" } },
            },
        }));
        var before = File.ReadAllBytes(manifest);
        var removed = new SkillInstaller().Uninstall(destination, null, null, dryRun: true);
        using var text = new StringWriter();

        new OutputWriter(text).WriteUninstallReport(removed, destination, dryRun: true);

        AssertPlainText(text.ToString());
        Assert.Contains("example-skill (example 1.0.0)", text.ToString());
        Assert.Equal("example", Assert.Single(removed).Package);
        Assert.Equal(version, Assert.Single(removed).Version);
        Assert.Equal(before, File.ReadAllBytes(manifest));
        Assert.Equal("installed guidance", File.ReadAllText(skillFile));
        Assert.Equal("handwritten guidance", File.ReadAllText(handwritten));
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void Operational_errors_are_sanitized_without_losing_line_breaks_or_stderr_routing(string newline)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();

        new OutputWriter(output, errors).WriteError(
            WithControls("Could not read the package.") + newline + WithControls("Restore it and try again."));

        Assert.Empty(output.ToString());
        AssertPlainText(errors.ToString());
        Assert.Equal(
            $"error: Could not read the package.{Environment.NewLine}Restore it and try again.{Environment.NewLine}",
            errors.ToString());
    }

    [Fact]
    public void Newlines_in_report_metadata_cannot_insert_additional_report_rows()
    {
        using var output = new StringWriter();

        new OutputWriter(output).WriteUninstallReport(
            [new TrackedSkill("Example\r\nPackage", "1.0.0", "example-skill")], @"C:\repo", dryRun: true);

        AssertPlainText(output.ToString());
        Assert.Contains("  example-skill (Example Package 1.0.0)", output.ToString());
        Assert.Equal(3, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Human_reports_preserve_ordinary_unicode_names()
    {
        using var output = new StringWriter();
        const string Skill = "\u6280\u80fd-\U0001f9ea";

        new OutputWriter(output).WriteUninstallReport(
            [new TrackedSkill("Caf\u00e9.Tools", "1.0.0", Skill)], @"C:\repo", dryRun: true);

        AssertPlainText(output.ToString());
        Assert.Contains($"{Skill} (Caf\u00e9.Tools 1.0.0)", output.ToString());
    }

    private static string WithControls(string text) => ClipboardControl + text + "\u001b[0m";

    private static void AssertPlainText(string text) =>
        Assert.True(
            !text.Any(character => char.IsControl(character) && character is not ('\r' or '\n') ||
                character is '\u202e' or '\u2066'),
            "Captured human-readable output contains unsafe terminal controls.");

    private static InstallResult ResultWithCollision() => new()
    {
        Target = @"C:\repo\App.sln",
        GlobalPackagesFolder = @"C:\packages",
        Destination = @"C:\repo\.agents\skills",
        PackagesScanned = 2,
        DryRun = false,
        Skills =
        [
            new BundledSkill(
                "Alpha.Widgets",
                "1.0.0",
                "shared-skill",
                @"C:\packages\alpha.widgets\1.0.0\skills\shared-skill",
                "shared-skill"),
        ],
        Skipped =
        [
            new SkippedSkill(
                "shared-skill",
                "Beta.Widgets",
                "2.0.0",
                "shared-skill",
                "conflicts with Alpha.Widgets 1.0.0 skill 'shared-skill', which was selected first"),
        ],
    };
}
