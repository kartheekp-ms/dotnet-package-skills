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
    public void Json_output_includes_structured_skipped_collisions()
    {
        using var output = new StringWriter();

        // Through JsonReport, because that is what the command does. Handing the writer a
        // domain record would test a path production no longer takes.
        new OutputWriter(output).WriteJson(JsonReport.For(ResultWithCollision()));

        var json = output.ToString();
        Assert.Contains("\"skipped\"", json);
        Assert.Contains("\"packageId\": \"Beta.Widgets\"", json);
        Assert.Contains("\"relativePath\": \"shared-skill\"", json);
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

        Assert.Contains("None of the scanned packages ship a skills/ folder", output.ToString());
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

    [Fact]
    public void Packages_that_were_never_extracted_are_not_said_to_ship_no_skills()
    {
        using var output = new StringWriter();
        var result = ResultWithCollision() with
        {
            Skills = [],
            Skipped = [],
            SkillsDiscovered = 0,
            NotOnDisk = ["Ghost.Package 9.9.9"],
        };

        new OutputWriter(output).WriteInstallReport(result, copied: true);

        var report = output.ToString();
        Assert.Contains("No bundled skills found.", report);
        // We could not look inside the package, so claiming it ships nothing would be a guess.
        Assert.DoesNotContain("ship a skills/ folder", report);
        Assert.Contains("not extracted in the NuGet cache", report);
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
            NotOnDisk = ["Ghost.Package 9.9.9"],
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
            NotOnDisk = [.. clean.NotOnDisk.Select(WithControls)],
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
        var package = "Example" + ClipboardControl;
        var version = "1.0.0" + ClipboardControl;
        var manifest = temp.CreateFile("dest/.dotnet-package-skills.json", JsonSerializer.Serialize(new
        {
            installed = new[] { new { package, version, skills = new[] { "example-skill" } } },
        }));
        var before = File.ReadAllBytes(manifest);
        var removed = new SkillInstaller().Uninstall(destination, null, null, dryRun: true);
        using var text = new StringWriter();
        using var json = new StringWriter();

        new OutputWriter(text).WriteUninstallReport(removed, destination, dryRun: true);
        new OutputWriter(json).WriteJson(JsonReport.ForUninstall(removed, destination, dryRun: true));

        AssertPlainText(text.ToString());
        Assert.Contains("example-skill (Example 1.0.0)", text.ToString());
        using var document = JsonDocument.Parse(json.ToString());
        var identity = document.RootElement.GetProperty("removed")[0];
        Assert.Equal(package, identity.GetProperty("packageId").GetString());
        Assert.Equal(version, identity.GetProperty("packageVersion").GetString());
        Assert.Equal(package, Assert.Single(removed).Package);
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
