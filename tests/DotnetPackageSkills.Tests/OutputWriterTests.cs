using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class OutputWriterTests
{
    [Fact]
    public void The_trust_notice_is_not_broken_mid_sentence()
    {
        using var output = new StringWriter();

        new OutputWriter(output).WriteInstallReport(ResultWithCollision(), copied: true);

        // It used to wrap at a guessed width, splitting "your coding / agent will follow
        // them" across two lines. Terminals wrap for themselves; a hard break just puts
        // the seam somewhere the reader's width did not ask for.
        var report = output.ToString();
        Assert.Contains("your coding agent will follow them.", report);
        Assert.Contains("Review them before relying on them.", report);
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
        Assert.Contains("from Beta.Widgets 2.0.0", report);
        Assert.Contains("selected first", report);
    }

    [Fact]
    public void Json_output_includes_structured_skipped_collisions()
    {
        using var output = new StringWriter();

        new OutputWriter(output).WriteJson(ResultWithCollision());

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
