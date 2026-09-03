using DotnetPackageSkills;
using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

/// <summary>
/// Renders every report shape and checks its blank lines.
/// </summary>
/// <remarks>
/// Vertical whitespace is invisible in a diff and obvious on a projector, so the rules that
/// keep it tidy are asserted rather than left to whoever edits the writer next.
/// </remarks>
public class OutputLayoutTests
{
    private static readonly BundledSkill[] TwoSkills =
    [
        new("Contoso.Widgets", "2.3.0", "contoso.widgets-usage", "/p/usage", "contoso.widgets-usage"),
        new("Mockly", "1.10.0", "mockly-usage", "/p/mockly", "mockly-usage"),
    ];

    public static TheoryData<string, string> EveryReport()
    {
        var data = new TheoryData<string, string>();

        foreach (var (name, report) in Reports())
        {
            data.Add(name, report);
        }

        return data;
    }

    private static IEnumerable<(string Name, string Report)> Reports()
    {
        yield return ("list", Render(Result() with { DryRun = true }, copied: false));
        yield return ("install", Render(Result(), copied: true));
        yield return ("install dry run", Render(Result() with { DryRun = true }, copied: true));
        yield return ("install nothing found", Render(
            Result() with { Skills = [], SkillsDiscovered = 0 }, copied: true));
        yield return ("install nothing chosen", Render(
            Result() with { Skills = [], SkillsDiscovered = 2 }, copied: true));
        yield return ("install with removals", Render(
            Result() with { Removed = [new TrackedSkill("Old.Package", "1.0.0", "old-skill")] },
            copied: true));
        yield return ("install with collisions", Render(
            Result() with
            {
                Skipped =
                [
                    new SkippedSkill("shared", "Beta", "2.0.0", "shared", "conflicts with Alpha 1.0.0"),
                ],
            },
            copied: true));
        yield return ("install with unextracted packages", Render(
            Result() with { Skills = [], SkillsDiscovered = 0, NotOnDisk = ["Ghost 9.9.9"] },
            copied: true));
        yield return ("uninstall", RenderUninstall(
            [new TrackedSkill("Contoso.Widgets", "2.3.0", "contoso.widgets-usage")], dryRun: false));
        yield return ("uninstall dry run", RenderUninstall(
            [new TrackedSkill("Contoso.Widgets", "2.3.0", "contoso.widgets-usage")], dryRun: true));
        yield return ("uninstall nothing to do", RenderUninstall([], dryRun: false));
        yield return ("cancelled", RenderCancelled());
    }

    [Theory]
    [MemberData(nameof(EveryReport))]
    public void No_report_starts_or_ends_with_a_blank_line(string name, string report)
    {
        var lines = Lines(report);

        Assert.False(lines[0].Length == 0, $"{name} opens with a blank line");
        Assert.False(lines[^1].Length == 0, $"{name} closes with a blank line");
    }

    [Theory]
    [MemberData(nameof(EveryReport))]
    public void No_report_has_two_blank_lines_together(string name, string report)
    {
        var lines = Lines(report);

        for (var index = 1; index < lines.Count; index++)
        {
            Assert.False(
                lines[index].Length == 0 && lines[index - 1].Length == 0,
                $"{name} has a double blank line at {index + 1}");
        }
    }

    [Theory]
    [MemberData(nameof(EveryReport))]
    public void No_report_wraps_a_sentence_onto_the_next_line(string name, string report)
    {
        // A line ending without terminal punctuation, followed by one starting lower case,
        // is prose someone hard-wrapped at a width the reader never asked for.
        var lines = Lines(report).Where(line => line.Length > 0 && !line.StartsWith(' ')).ToList();

        for (var index = 0; index < lines.Count - 1; index++)
        {
            var ends = lines[index].TrimEnd();
            var next = lines[index + 1];

            Assert.False(
                ends.Length > 0 && ends[^1] is not ('.' or ':' or '!' or '?') && char.IsLower(next[0]),
                $"{name}: '{ends}' looks wrapped into '{next}'");
        }
    }

    [Fact]
    public void A_skill_and_the_package_it_came_from_share_one_line()
    {
        var report = Render(Result(), copied: true);

        Assert.Contains("contoso.widgets-usage (Contoso.Widgets 2.3.0)", report);
        // The old shape put "from Package Version" on its own indented line, doubling the
        // length of every list to say something the brackets say for free.
        Assert.DoesNotContain("      from ", report);
    }

    [Fact]
    public void A_twelve_skill_list_is_twelve_lines_of_skills()
    {
        var skills = Enumerable.Range(1, 12)
            .Select(number => new BundledSkill(
                "Contoso.Widgets", "2.3.0", $"skill-{number:00}", $"/p/{number}", $"skill-{number:00}"))
            .ToList();

        var report = Render(Result() with { Skills = skills }, copied: true);

        Assert.Equal(12, Lines(report).Count(line => line.StartsWith("  skill-", StringComparison.Ordinal)));
    }

    private static List<string> Lines(string report) =>
        [.. report.TrimEnd('\r', '\n').Split(Environment.NewLine)];

    private static string Render(InstallResult result, bool copied)
    {
        using var output = new StringWriter();
        new OutputWriter(output).WriteInstallReport(result, copied);
        return output.ToString();
    }

    private static string RenderUninstall(IReadOnlyList<TrackedSkill> removed, bool dryRun)
    {
        using var output = new StringWriter();
        new OutputWriter(output).WriteUninstallReport(removed, @"C:\repo\.agents\skills", dryRun);
        return output.ToString();
    }

    private static string RenderCancelled()
    {
        using var output = new StringWriter();
        new OutputWriter(output).WriteCancelled();
        return output.ToString();
    }

    private static InstallResult Result() => new()
    {
        Target = @"C:\repo\App.slnx",
        GlobalPackagesFolder = @"C:\packages",
        Destination = @"C:\repo\.agents\skills",
        PackagesScanned = 3,
        DryRun = false,
        Skills = TwoSkills,
        SkillsDiscovered = TwoSkills.Length,
    };
}
