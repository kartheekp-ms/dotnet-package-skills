using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class OutputWriterTests
{
    [Fact]
    public void Sync_report_warns_about_skipped_collisions()
    {
        using var output = new StringWriter();
        var result = ResultWithCollision();

        new OutputWriter(output).WriteSyncReport(result, copied: true);

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

    private static SyncResult ResultWithCollision() => new()
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
