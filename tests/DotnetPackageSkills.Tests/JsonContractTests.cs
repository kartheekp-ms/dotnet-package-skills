using System.Text.Json;
using DotnetPackageSkills;
using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

/// <summary>
/// Pins the wire shape of <c>--json</c>.
/// </summary>
/// <remarks>
/// The commands once serialized their result records straight to stdout, so the contract was
/// whatever the domain happened to look like that week, and two vocabularies reached scripts at
/// once: skills said packageId/packageVersion/skillName and removed said package/version/skill.
/// These tests fail on the day someone reintroduces either problem.
/// </remarks>
public class JsonContractTests
{
    /// <summary>Every name a skill is allowed to be described by, in any command.</summary>
    private static readonly string[] SkillVocabulary =
        ["packageId", "packageVersion", "skillName", "sourcePath", "relativePath", "reason"];

    /// <summary>The three that identify a skill, required wherever one appears.</summary>
    private static readonly string[] Identity = ["packageId", "packageVersion", "skillName"];

    [Fact]
    public void Every_skill_in_every_command_is_described_with_one_vocabulary()
    {
        foreach (var (command, payload) in EveryPayload())
        {
            foreach (var (array, skill) in SkillsIn(payload))
            {
                var names = skill.EnumerateObject().Select(property => property.Name).ToList();

                Assert.All(names, name => Assert.Contains(name, SkillVocabulary));

                foreach (var required in Identity)
                {
                    Assert.True(
                        names.Contains(required),
                        $"{command} {array}[] is missing '{required}'. It has: {string.Join(", ", names)}");
                }
            }
        }
    }

    [Fact]
    public void A_removed_skill_reads_the_same_whichever_command_reported_it()
    {
        var skill = new TrackedSkill("Contoso.Widgets", "2.3.0", "contoso.widgets-usage");

        var fromInstall = Only(Render(JsonReport.For(Result() with { Removed = [skill] })), "removed");
        var fromUninstall = Only(Render(JsonReport.ForUninstall([skill], @"C:\repo", dryRun: false)), "removed");

        // Raw text, because JsonElement is a struct with no value equality: Assert.Equal on two
        // of them compares identity and fails even when the bytes match.
        Assert.Equal(fromUninstall.GetRawText(), fromInstall.GetRawText());
    }

    [Fact]
    public void Install_and_uninstall_agree_on_the_top_level_names_they_share()
    {
        var install = Keys(Render(JsonReport.For(Result())));
        var uninstall = Keys(Render(JsonReport.ForUninstall([], @"C:\repo", dryRun: false)));

        Assert.Equal(["destination", "dryRun", "removed"], uninstall);
        Assert.All(uninstall, name => Assert.Contains(name, install));
    }

    [Fact]
    public void Install_reports_the_keys_a_script_is_promised()
    {
        Assert.Equal(
            [
                "target", "globalPackagesFolder", "destination", "packagesScanned", "dryRun",
                "skills", "skillsDiscovered", "removed", "skipped", "notOnDisk",
            ],
            Keys(Render(JsonReport.For(Result()))));
    }

    [Fact]
    public void Naming_packages_outright_leaves_target_out_rather_than_null()
    {
        var json = Render(JsonReport.For(Result() with { Target = null }));

        Assert.DoesNotContain("target", Keys(json));
    }

    [Fact]
    public void A_skipped_skill_keeps_its_reason()
    {
        var result = Result() with
        {
            Skipped = [new SkippedSkill("shared", "Beta", "2.0.0", "shared", "conflicts with Alpha 1.0.0")],
        };

        var skipped = Only(Render(JsonReport.For(result)), "skipped");

        Assert.Equal("conflicts with Alpha 1.0.0", skipped.GetProperty("reason").GetString());
        Assert.Equal("Beta", skipped.GetProperty("packageId").GetString());
    }

    [Fact]
    public void A_removed_skill_claims_no_source_path_because_it_is_no_longer_there()
    {
        var removed = Only(
            Render(JsonReport.ForUninstall(
                [new TrackedSkill("Contoso.Widgets", "2.3.0", "contoso.widgets-usage")],
                @"C:\repo",
                dryRun: false)),
            "removed");

        Assert.False(removed.TryGetProperty("sourcePath", out _));
    }

    [Fact]
    public void Nothing_to_report_is_still_an_object_a_script_can_parse()
    {
        foreach (var json in new[]
        {
            Render(JsonReport.For(Result() with { Skills = [], SkillsDiscovered = 0 })),
            Render(JsonReport.ForUninstall([], @"C:\repo", dryRun: false)),
        })
        {
            using var document = JsonDocument.Parse(json);
            Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("removed").ValueKind);
        }
    }

    private static IEnumerable<(string Command, string Json)> EveryPayload()
    {
        var result = Result() with
        {
            Removed = [new TrackedSkill("Old.Package", "1.0.0", "old-skill")],
            Skipped = [new SkippedSkill("shared", "Beta", "2.0.0", "shared", "collides")],
        };

        yield return ("install", Render(JsonReport.For(result)));
        yield return ("list", Render(JsonReport.For(result with { DryRun = true })));
        yield return ("uninstall", Render(JsonReport.ForUninstall(
            [new TrackedSkill("Contoso.Widgets", "2.3.0", "contoso.widgets-usage")],
            @"C:\repo",
            dryRun: false)));
    }

    private static IEnumerable<(string Array, JsonElement Skill)> SkillsIn(string json)
    {
        using var document = JsonDocument.Parse(json);

        foreach (var name in new[] { "skills", "removed", "skipped" })
        {
            if (!document.RootElement.TryGetProperty(name, out var array))
            {
                continue;
            }

            foreach (var skill in array.EnumerateArray())
            {
                // Cloned because the document is disposed before the caller reads it.
                yield return (name, skill.Clone());
            }
        }
    }

    private static JsonElement Only(string json, string arrayName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(arrayName).EnumerateArray().Single().Clone();
    }

    private static List<string> Keys(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateObject().Select(property => property.Name)];
    }

    /// <summary>Goes through the writer so the test sees the serializer settings shipped.</summary>
    private static string Render(object payload)
    {
        using var output = new StringWriter();
        new OutputWriter(output).WriteJson(payload);
        return output.ToString();
    }

    private static InstallResult Result() => new()
    {
        Target = @"C:\repo\App.slnx",
        GlobalPackagesFolder = @"C:\packages",
        Destination = @"C:\repo\.agents\skills",
        PackagesScanned = 3,
        DryRun = false,
        Skills =
        [
            new BundledSkill("Contoso.Widgets", "2.3.0", "contoso.widgets-usage", "/p/usage", "contoso.widgets-usage"),
        ],
        SkillsDiscovered = 1,
    };
}
