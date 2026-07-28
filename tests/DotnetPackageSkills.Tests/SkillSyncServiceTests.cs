using DotnetPackageSkills.Infrastructure;
using DotnetPackageSkills.NuGet;

namespace DotnetPackageSkills.Tests;

/// <summary>
/// Exercises the whole flow with the dotnet CLI stubbed out, so the wiring between
/// listing, path resolution, discovery, and installation is covered without a restore.
/// </summary>
public class SkillSyncServiceTests
{
    private sealed class FakeDotnet(string globalPackagesFolder, string listPackageJson) : IProcessRunner
    {
        public List<string> Invocations { get; } = [];

        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null)
        {
            var line = string.Join(' ', arguments);
            Invocations.Add(line);

            if (arguments.Contains("locals"))
            {
                return new ProcessResult(0, $"global-packages: {globalPackagesFolder}", string.Empty);
            }

            if (arguments.Contains("list"))
            {
                return new ProcessResult(0, listPackageJson, string.Empty);
            }

            if (arguments.Contains("restore"))
            {
                return new ProcessResult(0, "Restore succeeded.", string.Empty);
            }

            throw new InvalidOperationException($"Unexpected invocation: {line}");
        }
    }

    private static string Json(params (string Id, string Version)[] packages)
    {
        var entries = packages.Select(p => $$"""{ "id": "{{p.Id}}", "resolvedVersion": "{{p.Version}}" }""");

        return $$"""
            {
              "projects": [
                {
                  "frameworks": [
                    { "framework": "net8.0", "topLevelPackages": [ {{string.Join(",", entries)}} ] }
                  ]
                }
              ]
            }
            """;
    }

    private static SyncRequest Request(TempDirectory temp) => new()
    {
        Destination = ".agents/skills",
        WorkingDirectory = temp.Path,
    };

    [Fact]
    public void Sync_copies_skills_from_packages_that_ship_them_and_ignores_the_rest()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreateDirectory("packages", "newtonsoft.json", "13.0.3");

        var runner = new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"), ("Newtonsoft.Json", "13.0.3")));
        var result = new SkillSyncService(runner).Sync(Request(temp));

        Assert.Equal(2, result.PackagesScanned);
        Assert.Equal("mockly/1.10.0/mockly", Assert.Single(result.Skills).RelativePath);
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "mockly", "1.10.0", "mockly", "SKILL.md")));
    }

    [Fact]
    public void Sync_reports_packages_that_are_resolved_but_not_extracted()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreateDirectory("packages");

        var runner = new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0")));
        var result = new SkillSyncService(runner).Sync(Request(temp));

        Assert.Equal("Mockly 1.10.0", Assert.Single(result.NotOnDisk));
        Assert.Empty(result.Skills);
    }

    [Fact]
    public void Sync_auto_detects_the_solution_when_no_target_is_given()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreateDirectory("packages");

        var runner = new FakeDotnet(temp.Combine("packages"), Json());
        var result = new SkillSyncService(runner).Sync(Request(temp));

        Assert.EndsWith("MyApp.sln", result.Target);
    }

    [Fact]
    public void Sync_honours_a_custom_destination()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");

        var runner = new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0")));
        new SkillSyncService(runner).Sync(Request(temp) with { Destination = ".claude/skills" });

        Assert.True(File.Exists(temp.Combine(".claude", "skills", "mockly", "1.10.0", "mockly", "SKILL.md")));
    }

    [Fact]
    public void Discover_does_not_write_to_the_destination()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");

        var runner = new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0")));
        var result = new SkillSyncService(runner).Discover(Request(temp));

        Assert.Single(result.Skills);
        Assert.False(Directory.Exists(temp.Combine(".agents")));
    }

    [Fact]
    public void Sync_asks_for_transitive_packages_only_when_requested()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreateDirectory("packages");

        var runner = new FakeDotnet(temp.Combine("packages"), Json());
        new SkillSyncService(runner).Sync(Request(temp) with { IncludeTransitive = true });

        Assert.Contains(runner.Invocations, line => line.Contains("--include-transitive"));
    }

    [Fact]
    public void Sync_passes_the_target_before_the_package_verb()
    {
        // `dotnet list <TARGET> package` is the required order; the reverse silently
        // lists the packages of whatever project is in the current directory instead.
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreateDirectory("packages");

        var runner = new FakeDotnet(temp.Combine("packages"), Json());
        new SkillSyncService(runner).Sync(Request(temp));

        var listCall = Assert.Single(runner.Invocations, line => line.StartsWith("list", StringComparison.Ordinal));
        Assert.Matches(@"^list .*MyApp\.sln package ", listCall);
    }

    [Fact]
    public void An_upgrade_replaces_the_previous_version_end_to_end()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Mockly", "1.11.0", "mockly");

        var service = new SkillSyncService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"))));
        service.Sync(Request(temp));

        var upgraded = new SkillSyncService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.11.0"))));
        var result = upgraded.Sync(Request(temp));

        Assert.Equal("mockly/1.10.0/mockly", Assert.Single(result.Removed).Path);
        Assert.False(Directory.Exists(temp.Combine(".agents", "skills", "mockly", "1.10.0")));
        Assert.True(Directory.Exists(temp.Combine(".agents", "skills", "mockly", "1.11.0", "mockly")));
    }

    [Fact]
    public void A_solution_whose_projects_disagree_on_a_version_gets_a_folder_per_version()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Mockly", "1.11.0", "mockly");

        // Two projects in one solution, each pinned to a different version of the same
        // package. Both are legitimately in use, so both skills have to be available.
        const string json = """
            {
              "projects": [
                {
                  "path": "/repo/src/Api/Api.csproj",
                  "frameworks": [
                    { "framework": "net8.0", "topLevelPackages": [ { "id": "Mockly", "resolvedVersion": "1.10.0" } ] }
                  ]
                },
                {
                  "path": "/repo/src/Worker/Worker.csproj",
                  "frameworks": [
                    { "framework": "net8.0", "topLevelPackages": [ { "id": "Mockly", "resolvedVersion": "1.11.0" } ] }
                  ]
                }
              ]
            }
            """;

        var result = new SkillSyncService(new FakeDotnet(temp.Combine("packages"), json)).Sync(Request(temp));

        Assert.Equal(
            ["mockly/1.10.0/mockly", "mockly/1.11.0/mockly"],
            result.Skills.Select(s => s.RelativePath).Order());
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "mockly", "1.10.0", "mockly", "SKILL.md")));
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "mockly", "1.11.0", "mockly", "SKILL.md")));
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Sync_takes_skills_from_an_explicitly_named_package_without_a_project()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");

        // No solution or project exists in the temp directory at all.
        var runner = new FakeDotnet(temp.Combine("packages"), Json());
        var result = new SkillSyncService(runner).Sync(
            Request(temp) with { Packages = [PackageCoordinate.Parse("Mockly@1.10.0")] });

        Assert.Null(result.Target);
        Assert.Equal("mockly/1.10.0/mockly", Assert.Single(result.Skills).RelativePath);
        Assert.DoesNotContain(runner.Invocations, line => line.StartsWith("list", StringComparison.Ordinal));
    }

    [Fact]
    public void Naming_a_package_explicitly_does_not_prune_skills_installed_from_a_project()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Contoso.Widgets", "2.3.0", "widget-usage");

        var service = new SkillSyncService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"))));
        service.Sync(Request(temp));

        // Naming one package says nothing about the others, so this must be additive.
        var result = service.Sync(
            Request(temp) with { Packages = [PackageCoordinate.Parse("Contoso.Widgets@2.3.0")] });

        Assert.Empty(result.Removed);
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "mockly", "1.10.0", "mockly", "SKILL.md")));
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "contoso.widgets", "2.3.0", "widget-usage", "SKILL.md")));
    }

    [Fact]
    public void An_explicitly_named_package_that_is_not_restored_is_reported_not_thrown()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("packages");

        var runner = new FakeDotnet(temp.Combine("packages"), Json());
        var result = new SkillSyncService(runner).Sync(
            Request(temp) with { Packages = [PackageCoordinate.Parse("Mockly@9.9.9")] });

        Assert.Equal("Mockly 9.9.9", Assert.Single(result.NotOnDisk));
    }

    [Fact]
    public void Uninstall_can_target_one_version_of_a_package()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Mockly", "1.11.0", "mockly");

        var service = new SkillSyncService(new FakeDotnet(temp.Combine("packages"), Json()));
        service.Sync(Request(temp) with
        {
            Packages = [PackageCoordinate.Parse("Mockly@1.10.0"), PackageCoordinate.Parse("Mockly@1.11.0")],
        });

        service.Uninstall(".agents/skills", temp.Path, "Mockly", "1.10.0", dryRun: false);

        Assert.False(Directory.Exists(temp.Combine(".agents", "skills", "mockly", "1.10.0")));
        Assert.True(Directory.Exists(temp.Combine(".agents", "skills", "mockly", "1.11.0", "mockly")));
    }

    [Fact]
    public void Uninstall_reverses_an_install()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");

        var service = new SkillSyncService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"))));
        service.Sync(Request(temp));

        var removed = service.Uninstall(".agents/skills", temp.Path, packageId: null, packageVersion: null, dryRun: false);

        Assert.Single(removed);
        Assert.False(Directory.Exists(temp.Combine(".agents", "skills")));
    }
}
