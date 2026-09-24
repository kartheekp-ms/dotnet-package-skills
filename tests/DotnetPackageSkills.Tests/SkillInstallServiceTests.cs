using DotnetPackageSkills.Infrastructure;
using DotnetPackageSkills.NuGet;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

/// <summary>
/// Exercises the whole flow with the dotnet CLI stubbed out, so the wiring between
/// listing, path resolution, discovery, and installation is covered without a restore.
/// </summary>
public class SkillInstallServiceTests
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

    private static InstallRequest Request(TempDirectory temp) => new()
    {
        Destination = ".agents/skills",
        WorkingDirectory = temp.Path,
        GlobalPackagesOverride = temp.Combine("packages"),
    };

    [Fact]
    public void Install_copies_skills_from_packages_that_ship_them_and_ignores_the_rest()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreateDirectory("packages", "newtonsoft.json", "13.0.3");

        var runner = new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"), ("Newtonsoft.Json", "13.0.3")));
        var result = new SkillInstallService(runner).Install(Request(temp));

        Assert.Equal(2, result.PackagesScanned);
        Assert.Equal("mockly", Assert.Single(result.Skills).RelativePath);
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "mockly", "SKILL.md")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Target_install_rejects_incomplete_discovery_before_changing_skills(bool dryRun)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Existing", "1.0.0", "existing");
        var initial = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Existing", "1.0.0"))));
        initial.Install(Request(temp));
        var manifestPath = temp.Combine(".agents", "skills", InstallManifest.FileName);
        var before = File.ReadAllBytes(manifestPath);

        var runner = new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0")));
        var error = Assert.Throws<PackageSkillsException>(() =>
            new SkillInstallService(runner).Install(Request(temp) with { DryRun = dryRun }));

        Assert.Contains("Mockly 1.10.0", error.Message);
        Assert.Contains("restore", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(manifestPath));
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "existing", "SKILL.md")));
    }

    [Fact]
    public void Discovery_treats_a_package_missing_from_the_cache_as_one_without_skills()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreateDirectory("packages");
        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Missing", "1.0.0"))));

        var result = service.Discover(Request(temp));

        Assert.Equal(1, result.PackagesScanned);
        Assert.Empty(result.Skills);
        Assert.Empty(result.Skipped);
        Assert.False(Directory.Exists(result.Destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_target_packages_also_block_refreshing_available_skills(bool interactive)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        var package = temp.CreatePackageWithSkill("Present", "1.0.0", "present");
        new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Present", "1.0.0"))))
            .Install(Request(temp));
        var destination = temp.Combine(".agents", "skills");
        var previous = File.ReadAllBytes(Path.Combine(destination, "present", "SKILL.md"));
        var manifest = File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName));
        File.WriteAllText(Path.Combine(package, "skills", "present", "SKILL.md"), "new source content");
        var service = new SkillInstallService(new FakeDotnet(
            temp.Combine("packages"), Json(("Present", "1.0.0"), ("Missing", "2.0.0"))));
        var discovered = service.Discover(Request(temp));

        Assert.Throws<PackageSkillsException>(() =>
        {
            if (interactive)
            {
                service.PrepareInteractiveInstall(
                    Request(temp), discovered, SkillInstallService.InstalledSkills(destination, temp.Path));
            }
            else
            {
                service.Install(Request(temp), discovered, null);
            }
        });

        Assert.Equal(previous, File.ReadAllBytes(Path.Combine(destination, "present", "SKILL.md")));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName)));
    }

    [Fact]
    public void Preparing_a_picker_excludes_conflicting_candidates_without_losing_the_warning()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("Alpha", "1.0.0", "shared");
        temp.CreatePackageWithSkill("Beta", "2.0.0", "shared");
        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json()));
        service.Install(Request(temp) with { Packages = [PackageCoordinate.Parse("Alpha@1.0.0")] });
        var request = Request(temp) with { Packages = [PackageCoordinate.Parse("Beta@2.0.0")] };
        var discovered = service.Discover(request);
        var installed = SkillInstallService.InstalledSkills(discovered.Destination, temp.Path);

        var prepared = service.PrepareInteractiveInstall(request, discovered, installed);
        var result = service.Install(request, prepared,
            new SkillChoice([]) { ExpectedInstalled = installed });

        Assert.Empty(prepared.Skills);
        Assert.Contains("managed for alpha", Assert.Single(result.Skipped).Reason);
        Assert.Empty(result.Removed);
        Assert.False(result.DryRun);
        Assert.Equal("alpha", Assert.Single(InstallManifest.Load(result.Destination).Packages).Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_interactive_target_install_stops_while_installed_skills_are_stale(bool versionChanged)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Mockly", "1.11.0", "mockly", "mockly-testing");
        temp.CreatePackageWithSkill("Contoso.Widgets", "2.3.0", "widget-usage");
        new SkillInstallService(new FakeDotnet(
            temp.Combine("packages"), Json(("Mockly", "1.10.0"), ("Contoso.Widgets", "2.3.0")))).Install(Request(temp));
        var destination = temp.Combine(".agents", "skills");
        var before = Snapshot(destination);
        var service = new SkillInstallService(new FakeDotnet(
            temp.Combine("packages"),
            versionChanged
                ? Json(("Mockly", "1.11.0"), ("Contoso.Widgets", "2.3.0"))
                : Json(("Mockly", "1.10.0"))));
        var discovered = service.Discover(Request(temp));

        var error = Assert.Throws<PackageSkillsException>(() => service.PrepareInteractiveInstall(
            Request(temp), discovered, SkillInstallService.InstalledSkills(destination, temp.Path)));

        Assert.Contains(
            versionChanged
                ? "1 installed skill doesn't match the target: mockly (mockly 1.10.0)"
                : "1 installed skill doesn't match the target: widget-usage (contoso.widgets 2.3.0)",
            error.Message);
        Assert.Contains("uninstall --stale", error.Message);
        Assert.Contains("No skills were changed", error.Message);
        Assert.Equal(before, Snapshot(destination));
    }

    [Fact]
    public void An_interactive_install_offers_only_the_skills_that_are_not_installed()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly-usage", "mockly-testing");
        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"))));
        var first = service.Discover(Request(temp));
        service.Install(Request(temp), first, new SkillChoice([.. first.Skills.Where(skill => skill.SkillName == "mockly-usage")]));
        var destination = temp.Combine(".agents", "skills");
        var installed = SkillInstallService.InstalledSkills(destination, temp.Path);

        var prepared = service.PrepareInteractiveInstall(Request(temp), service.Discover(Request(temp)), installed);

        Assert.Equal("mockly-testing", Assert.Single(prepared.Skills).SkillName);
        Assert.Empty(prepared.Removed);
        Assert.Empty(prepared.Unreferenced);
        Assert.False(Directory.Exists(Path.Combine(destination, "mockly-testing")));
    }

    [Fact]
    public void An_interactive_install_with_nothing_new_offers_an_empty_list()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"))));
        service.Install(Request(temp));
        var installed = SkillInstallService.InstalledSkills(temp.Combine(".agents", "skills"), temp.Path);

        var prepared = service.PrepareInteractiveInstall(Request(temp), service.Discover(Request(temp)), installed);

        Assert.Empty(prepared.Skills);
        Assert.Equal(1, prepared.SkillsDiscovered);
        Assert.Empty(prepared.Skipped);
    }

    [Fact]
    public void An_interactive_install_of_a_named_package_stops_when_another_version_is_installed()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Mockly", "1.11.0", "mockly", "mockly-testing");
        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json()));
        service.Install(Request(temp) with { Packages = [PackageCoordinate.Parse("Mockly@1.10.0")] });
        var destination = temp.Combine(".agents", "skills");
        var before = Snapshot(destination);
        var request = Request(temp) with { Packages = [PackageCoordinate.Parse("Mockly@1.11.0")] };

        var error = Assert.Throws<PackageSkillsException>(() => service.PrepareInteractiveInstall(
            request, service.Discover(request), SkillInstallService.InstalledSkills(destination, temp.Path)));

        Assert.Contains("Mockly 1.10.0 is already installed", error.Message);
        Assert.Contains("'dotnet package-skills uninstall --package Mockly'", error.Message);
        Assert.Equal(before, Snapshot(destination));
    }

    [Theory]
    [InlineData(".agents/skills", null, "--stale", true, "dotnet package-skills uninstall --stale")]
    [InlineData(".agents/skills/", null, "--stale", true, "dotnet package-skills uninstall --stale")]
    [InlineData(".claude/skills", null, "--stale", true, "dotnet package-skills uninstall --stale --destination .claude/skills")]
    [InlineData(
        "my skills", "src/My App.slnx", "--stale", true,
        "dotnet package-skills uninstall --stale --target \"src/My App.slnx\" --destination \"my skills\"")]
    [InlineData(
        ".claude/skills", "src/App.slnx", "--package Mockly", false,
        "dotnet package-skills uninstall --package Mockly --destination .claude/skills")]
    [InlineData(
        @"C:\src\skills", null, "--stale", true,
        "dotnet package-skills uninstall --stale --destination \"C:\\src\\skills\"")]
    public void Suggested_commands_repeat_the_target_and_destination_that_were_used(
        string destination, string? target, string arguments, bool withTarget, string expected)
    {
        // A suggestion is only useful if running it as printed acts on the same skills folder,
        // compared against the same project.
        using var temp = new TempDirectory();
        var request = Request(temp) with { Destination = destination, Target = target };

        Assert.Equal(expected, SkillInstallService.UninstallCommand(request, arguments, withTarget));
    }

    [Fact]
    public void A_suggested_command_leaves_out_a_destination_that_is_the_default_spelled_in_full()
    {
        using var temp = new TempDirectory();
        var request = Request(temp) with { Destination = temp.Combine(".agents", "skills") };

        Assert.Equal("dotnet package-skills uninstall --stale", SkillInstallService.UninstallCommand(request, "--stale"));
    }

    [Fact]
    public void The_stale_hint_and_the_stale_stop_name_the_destination_that_was_used()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Contoso.Widgets", "2.3.0", "widget-usage");
        var request = Request(temp) with { Destination = ".claude/skills" };
        new SkillInstallService(new FakeDotnet(
            temp.Combine("packages"), Json(("Mockly", "1.10.0"), ("Contoso.Widgets", "2.3.0")))).Install(request);
        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"))));

        var result = service.Install(request);
        var error = Assert.Throws<PackageSkillsException>(() => service.PrepareInteractiveInstall(
            request, service.Discover(request), SkillInstallService.InstalledSkills(".claude/skills", temp.Path)));

        const string Command = "dotnet package-skills uninstall --stale --destination .claude/skills";
        Assert.Equal(Command, result.StaleCommand);
        Assert.Contains($"Run '{Command}' first", error.Message);
    }

    [Fact]
    public void The_other_version_stop_names_the_destination_that_was_used()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Mockly", "1.11.0", "mockly");
        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json()));
        var request = Request(temp) with { Destination = ".claude/skills" };
        service.Install(request with { Packages = [PackageCoordinate.Parse("Mockly@1.10.0")] });
        var upgrade = request with { Packages = [PackageCoordinate.Parse("Mockly@1.11.0")] };

        var error = Assert.Throws<PackageSkillsException>(() => service.PrepareInteractiveInstall(
            upgrade, service.Discover(upgrade), SkillInstallService.InstalledSkills(".claude/skills", temp.Path)));

        Assert.Contains("'dotnet package-skills uninstall --package Mockly --destination .claude/skills' first", error.Message);
    }

    [Fact]
    public void A_version_change_that_would_hand_a_skill_to_another_package_stops_with_a_command_for_this_destination()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Alpha", "1.0.0", "alpha-usage", "shared");
        temp.CreatePackageWithSkill("Alpha", "2.0.0", "alpha-usage");
        temp.CreatePackageWithSkill("Beta", "1.0.0", "shared");
        var request = Request(temp) with { Destination = ".claude/skills" };
        new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Alpha", "1.0.0")))).Install(request);
        var destination = temp.Combine(".claude", "skills");
        var before = Snapshot(destination);
        var service = new SkillInstallService(new FakeDotnet(
            temp.Combine("packages"), Json(("Alpha", "2.0.0"), ("Beta", "1.0.0"))));

        var error = Assert.Throws<PackageSkillsException>(() => service.Install(request));

        Assert.Contains("Alpha 2.0.0 no longer ships the installed skill 'shared'", error.Message);
        Assert.Contains("'dotnet package-skills uninstall --package Alpha --destination .claude/skills' first", error.Message);
        Assert.Equal(before, Snapshot(destination));
    }

    [Fact]
    public void An_interactive_install_of_a_named_package_ignores_other_installed_packages()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Contoso.Widgets", "2.3.0", "widget-usage");
        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json()));
        service.Install(Request(temp) with { Packages = [PackageCoordinate.Parse("Contoso.Widgets@2.3.0")] });
        var request = Request(temp) with { Packages = [PackageCoordinate.Parse("Mockly@1.10.0")] };
        var installed = SkillInstallService.InstalledSkills(temp.Combine(".agents", "skills"), temp.Path);

        var prepared = service.PrepareInteractiveInstall(request, service.Discover(request), installed);

        Assert.Equal("mockly", Assert.Single(prepared.Skills).SkillName);
    }

    [Fact]
    public void A_package_that_disappears_after_the_preview_also_blocks_acceptance()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Present", "1.0.0", "present");
        var emptyPackage = temp.CreateDirectory("packages", "empty", "1.0.0");
        var service = new SkillInstallService(new FakeDotnet(
            temp.Combine("packages"), Json(("Present", "1.0.0"), ("Empty", "1.0.0"))));
        var discovered = service.Discover(Request(temp));
        var prepared = service.PrepareInteractiveInstall(Request(temp), discovered, []);
        Directory.Delete(emptyPackage);

        var error = Assert.Throws<PackageSkillsException>(() =>
            service.Install(Request(temp), prepared, new SkillChoice(prepared.Skills)));

        Assert.Contains("Empty 1.0.0", error.Message);
        Assert.False(Directory.Exists(prepared.Destination));
    }

    [Fact]
    public void Installation_prefers_the_current_owners_upgrade_over_an_earlier_named_collision()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Zeta", "1.0.0", "shared");
        new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Zeta", "1.0.0"))))
            .Install(Request(temp));
        temp.CreatePackageWithSkill("Alpha", "1.0.0", "shared");
        var newer = temp.CreatePackageWithSkill("Zeta", "2.0.0", "shared");
        File.WriteAllText(Path.Combine(newer, "skills", "shared", "SKILL.md"), "updated owner");
        var service = new SkillInstallService(new FakeDotnet(
            temp.Combine("packages"), Json(("Alpha", "1.0.0"), ("Zeta", "2.0.0"))));

        var discovered = service.Discover(Request(temp));
        Assert.Equal("Alpha", Assert.Single(discovered.Skills).PackageId);
        var result = service.Install(Request(temp), discovered, choice: null);

        Assert.Equal("Zeta", Assert.Single(result.Skills).PackageId);
        Assert.Equal("Alpha", Assert.Single(result.Skipped).PackageId);
        Assert.Empty(result.Removed);
        Assert.Equal("updated owner", File.ReadAllText(Path.Combine(result.Destination, "shared", "SKILL.md")));
        Assert.Equal("2.0.0", Assert.Single(InstallManifest.Load(result.Destination).Packages).Value.Version);
    }

    [Fact]
    public void Repeating_equivalent_package_coordinates_does_not_create_self_collisions()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("Alpha", "1.0.0", "alpha");
        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json()));

        var result = service.Install(Request(temp) with
        {
            Packages = [
                PackageCoordinate.Parse("Alpha@1.0"),
                PackageCoordinate.Parse("alpha@1.0.0"),
                PackageCoordinate.Parse("Alpha@1.0.0.0"),
            ],
        });

        Assert.Equal(1, result.PackagesScanned);
        Assert.Single(result.Skills);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Install_auto_detects_the_solution_when_no_target_is_given()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreateDirectory("packages");

        var runner = new FakeDotnet(temp.Combine("packages"), Json());
        var result = new SkillInstallService(runner).Install(Request(temp));

        Assert.EndsWith("MyApp.sln", result.Target);
    }

    [Fact]
    public void Install_honours_a_custom_destination()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");

        var runner = new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0")));
        new SkillInstallService(runner).Install(Request(temp) with { Destination = ".claude/skills" });

        Assert.True(File.Exists(temp.Combine(".claude", "skills", "mockly", "SKILL.md")));
    }

    [Fact]
    public void Discover_does_not_write_to_the_destination()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");

        var runner = new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0")));
        var result = new SkillInstallService(runner).Discover(Request(temp));

        Assert.Single(result.Skills);
        Assert.False(Directory.Exists(temp.Combine(".agents")));
    }

    [Fact]
    public void Install_never_requests_transitive_packages()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreateDirectory("packages");

        var runner = new FakeDotnet(temp.Combine("packages"), Json());
        new SkillInstallService(runner).Install(Request(temp));

        Assert.DoesNotContain(runner.Invocations, line => line.Contains("--include-transitive"));
    }

    [Fact]
    public void Install_passes_the_target_before_the_package_verb()
    {
        // `dotnet list <TARGET> package` is the required order; the reverse silently
        // lists the packages of whatever project is in the current directory instead.
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreateDirectory("packages");

        var runner = new FakeDotnet(temp.Combine("packages"), Json());
        new SkillInstallService(runner).Install(Request(temp));

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

        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"))));
        service.Install(Request(temp));

        var upgraded = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.11.0"))));
        var result = upgraded.Install(Request(temp));

        Assert.Empty(result.Removed);
        Assert.True(Directory.Exists(temp.Combine(".agents", "skills", "mockly")));
        Assert.Equal(
            "1.11.0",
            Assert.Single(InstallManifest.Load(temp.Combine(".agents", "skills")).Packages).Value.Version);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_solution_whose_projects_disagree_on_a_version_stops_every_install_mode(bool interactive, bool dryRun)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Mockly", "1.11.0", "mockly");
        new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"))))
            .Install(Request(temp));
        var destination = temp.Combine(".agents", "skills");
        var before = Snapshot(destination);

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
        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), json));
        var request = Request(temp) with { DryRun = dryRun };

        var error = Assert.Throws<PackageSkillsException>(() =>
        {
            if (interactive)
            {
                service.PrepareInteractiveInstall(
                    request, service.Discover(request), SkillInstallService.InstalledSkills(destination, temp.Path));
            }
            else
            {
                service.Install(request);
            }
        });

        Assert.Contains("Mockly (1.10.0, 1.11.0)", error.Message);
        Assert.Contains("Central Package Management", error.Message);
        Assert.Contains("No skills were changed", error.Message);
        Assert.Equal(before, Snapshot(destination));
    }

    [Fact]
    public void Two_versions_of_a_package_that_ships_no_skills_also_stop_the_install()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreateDirectory("packages", "newtonsoft.json", "12.0.3");
        temp.CreateDirectory("packages", "newtonsoft.json", "13.0.3");
        var service = new SkillInstallService(new FakeDotnet(
            temp.Combine("packages"),
            Json(("Mockly", "1.10.0"), ("Newtonsoft.Json", "12.0.3"), ("Newtonsoft.Json", "13.0.3"))));

        var error = Assert.Throws<PackageSkillsException>(() => service.Install(Request(temp)));

        Assert.Contains("Newtonsoft.Json (12.0.3, 13.0.3)", error.Message);
        Assert.DoesNotContain("Mockly", error.Message);
        Assert.False(Directory.Exists(temp.Combine(".agents")));
    }

    [Fact]
    public void List_still_shows_what_each_version_ships_when_a_package_has_two()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Mockly", "1.11.0", "mockly", "mockly-testing");
        var service = new SkillInstallService(new FakeDotnet(
            temp.Combine("packages"), Json(("Mockly", "1.10.0"), ("Mockly", "1.11.0"))));

        var result = service.Discover(Request(temp));

        Assert.Equal(["mockly", "mockly-testing"], result.Skills.Select(skill => skill.SkillName));
        Assert.Equal("1.11.0", Assert.Single(result.Skipped).PackageVersion);
        Assert.False(Directory.Exists(temp.Combine(".agents")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Naming_one_package_with_two_versions_stops_the_install(bool interactive)
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Mockly", "1.11.0", "mockly-testing");
        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json()));
        var request = Request(temp) with
        {
            Packages = [PackageCoordinate.Parse("Mockly@1.10.0"), PackageCoordinate.Parse("mockly@1.11")],
        };

        var error = Assert.Throws<PackageSkillsException>(() =>
        {
            if (interactive)
            {
                service.PrepareInteractiveInstall(request, service.Discover(request), []);
            }
            else
            {
                service.Install(request);
            }
        });

        Assert.Contains("Mockly (1.10.0, 1.11)", error.Message);
        Assert.Contains("one version per package", error.Message);
        Assert.Contains("No skills were changed", error.Message);
        Assert.False(Directory.Exists(temp.Combine(".agents")));
    }

    [Fact]
    public void Skills_from_different_packages_that_share_a_name_keep_the_first_and_warn()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Alpha.Widgets", "1.0.0", "shared-skill");
        temp.CreatePackageWithSkill("Beta.Widgets", "1.0.0", "SHARED-SKILL");

        var runner = new FakeDotnet(
            temp.Combine("packages"),
            Json(("Beta.Widgets", "1.0.0"), ("Alpha.Widgets", "1.0.0")));
        var result = new SkillInstallService(runner).Install(Request(temp));

        Assert.Equal("Alpha.Widgets", Assert.Single(result.Skills).PackageId);
        Assert.Equal("Beta.Widgets", Assert.Single(result.Skipped).PackageId);
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "shared-skill", "SKILL.md")));
    }

    [Fact]
    public void Install_takes_skills_from_an_explicitly_named_package_without_a_project()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");

        // No solution or project exists in the temp directory at all.
        var runner = new FakeDotnet(temp.Combine("packages"), Json());
        var result = new SkillInstallService(runner).Install(
            Request(temp) with { Packages = [PackageCoordinate.Parse("Mockly@1.10.0")] });

        Assert.Null(result.Target);
        Assert.Equal("mockly", Assert.Single(result.Skills).RelativePath);
        Assert.DoesNotContain(runner.Invocations, line => line.StartsWith("list", StringComparison.Ordinal));
    }

    [Fact]
    public void Naming_a_package_explicitly_does_not_prune_skills_installed_from_a_project()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Contoso.Widgets", "2.3.0", "widget-usage");

        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"))));
        service.Install(Request(temp));

        // Naming one package says nothing about the others, so this must be additive.
        var result = service.Install(
            Request(temp) with { Packages = [PackageCoordinate.Parse("Contoso.Widgets@2.3.0")] });

        Assert.Empty(result.Removed);
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "mockly", "SKILL.md")));
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "widget-usage", "SKILL.md")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_target_install_keeps_skills_whose_package_left_the_project_and_reports_them(bool dryRun)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Contoso.Widgets", "2.3.0", "widget-usage");
        new SkillInstallService(new FakeDotnet(
            temp.Combine("packages"), Json(("Mockly", "1.10.0"), ("Contoso.Widgets", "2.3.0")))).Install(Request(temp));
        var destination = temp.Combine(".agents", "skills");
        var before = Snapshot(destination);

        var result = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"))))
            .Install(Request(temp) with { DryRun = dryRun });

        Assert.Empty(result.Removed);
        Assert.Equal(new TrackedSkill("contoso.widgets", "2.3.0", "widget-usage"), Assert.Single(result.Unreferenced));
        Assert.True(File.Exists(Path.Combine(destination, "widget-usage", "SKILL.md")));
        Assert.Equal(before, Snapshot(destination));
    }

    [Fact]
    public void A_target_upgrade_removes_the_skills_the_new_version_dropped()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly-usage", "mockly-migration");
        temp.CreatePackageWithSkill("Mockly", "1.11.0", "mockly-usage");
        new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"))))
            .Install(Request(temp));

        var result = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.11.0"))))
            .Install(Request(temp));

        Assert.Equal("mockly-usage", Assert.Single(result.Skills).SkillName);
        Assert.Equal(new TrackedSkill("mockly", "1.10.0", "mockly-migration"), Assert.Single(result.Removed));
        Assert.Empty(result.Unreferenced);
        Assert.False(Directory.Exists(temp.Combine(".agents", "skills", "mockly-migration")));
        Assert.Equal("1.11.0", InstallManifest.Load(result.Destination).Packages["mockly"].Version);
    }

    [Fact]
    public void Naming_a_newer_version_upgrades_that_package_and_leaves_the_others_alone()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly-usage", "mockly-migration");
        temp.CreatePackageWithSkill("Mockly", "1.11.0", "mockly-usage");
        temp.CreatePackageWithSkill("Contoso.Widgets", "2.3.0", "widget-usage");
        var service = new SkillInstallService(new FakeDotnet(
            temp.Combine("packages"), Json(("Mockly", "1.10.0"), ("Contoso.Widgets", "2.3.0"))));
        service.Install(Request(temp));

        var result = service.Install(Request(temp) with { Packages = [PackageCoordinate.Parse("Mockly@1.11.0")] });

        Assert.Equal("mockly-migration", Assert.Single(result.Removed).Skill);
        Assert.Empty(result.Unreferenced);
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "widget-usage", "SKILL.md")));
        var manifest = InstallManifest.Load(result.Destination);
        Assert.Equal("1.11.0", manifest.Packages["mockly"].Version);
        Assert.Equal("2.3.0", manifest.Packages["contoso.widgets"].Version);
    }

    [Fact]
    public void A_named_version_missing_from_the_cache_never_removes_anything()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly-usage", "mockly-migration");
        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json()));
        service.Install(Request(temp) with { Packages = [PackageCoordinate.Parse("Mockly@1.10.0")] });
        var destination = temp.Combine(".agents", "skills");
        var before = Snapshot(destination);

        var result = service.Install(Request(temp) with { Packages = [PackageCoordinate.Parse("Mockly@1.11.0")] });

        Assert.Empty(result.Skills);
        Assert.Empty(result.Removed);
        Assert.Equal(before, Snapshot(destination));
    }

    [Fact]
    public void An_explicitly_named_package_missing_from_the_cache_installs_nothing_without_an_error()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("packages");

        var runner = new FakeDotnet(temp.Combine("packages"), Json());
        var result = new SkillInstallService(runner).Install(
            Request(temp) with { Packages = [PackageCoordinate.Parse("Mockly@9.9.9")] });

        Assert.Equal(1, result.PackagesScanned);
        Assert.Empty(result.Skills);
        Assert.Empty(result.Skipped);
        Assert.False(Directory.Exists(result.Destination));
    }

    [Fact]
    public void A_selection_can_take_only_some_of_one_packages_skills()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill(
            "Mockly",
            "1.10.0",
            "mockly-assertions",
            "mockly-testing",
            "mockly-usage",
            "mockly-verification");

        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json()));
        var request = Request(temp) with { Packages = [PackageCoordinate.Parse("Mockly@1.10.0")] };

        var discovered = service.Discover(request);
        Assert.Equal(4, discovered.Skills.Count);

        var chosen = discovered.Skills
            .Where(skill => skill.RelativePath is "mockly-assertions" or "mockly-usage")
            .ToList();

        var result = service.Install(request, discovered, new SkillChoice(chosen));

        Assert.Equal(
            ["mockly-assertions", "mockly-usage"],
            result.Skills.Select(skill => skill.RelativePath));
        Assert.False(Directory.Exists(temp.Combine(".agents", "skills", "mockly-testing")));
        Assert.False(Directory.Exists(temp.Combine(".agents", "skills", "mockly-verification")));
    }

    [Fact]
    public void A_selection_installs_only_the_skills_that_were_chosen()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Contoso.Widgets", "2.3.0", "widget-usage");

        var service = new SkillInstallService(new FakeDotnet(
            temp.Combine("packages"),
            Json(("Mockly", "1.10.0"), ("Contoso.Widgets", "2.3.0"))));

        var request = Request(temp);
        var discovered = service.Discover(request);
        var chosen = discovered.Skills.Where(skill => skill.RelativePath == "mockly").ToList();

        var result = service.Install(request, discovered, new SkillChoice(chosen));

        Assert.Equal("mockly", Assert.Single(result.Skills).RelativePath);
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "mockly", "SKILL.md")));
        Assert.False(Directory.Exists(temp.Combine(".agents", "skills", "widget-usage")));
    }

    [Fact]
    public void A_selection_never_removes_an_installed_skill_it_does_not_mention()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Contoso.Widgets", "2.3.0", "widget-usage");

        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json()));
        var request = Request(temp) with
        {
            Packages =
            [
                PackageCoordinate.Parse("Mockly@1.10.0"),
                PackageCoordinate.Parse("Contoso.Widgets@2.3.0"),
            ],
        };

        service.Install(request);

        var discovered = service.Discover(request);
        var keep = discovered.Skills.Where(skill => skill.RelativePath == "mockly").ToList();

        var result = service.Install(request, discovered, new SkillChoice(keep));

        Assert.Empty(result.Removed);
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "widget-usage", "SKILL.md")));
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "mockly", "SKILL.md")));
    }

    [Fact]
    public void A_selection_in_a_dry_run_writes_nothing()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");

        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"))));
        var request = Request(temp) with { DryRun = true };
        var discovered = service.Discover(request);

        var result = service.Install(request, discovered, new SkillChoice(discovered.Skills));

        Assert.Single(result.Skills);
        Assert.False(Directory.Exists(temp.Combine(".agents", "skills")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_selection_never_removes_skills_the_packages_no_longer_offer(bool noCandidates)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "current", "stale");
        var initial = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"))));
        initial.Install(Request(temp));
        var service = noCandidates
            ? new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json()))
            : initial;
        if (!noCandidates)
        {
            Directory.Delete(temp.Combine("packages", "mockly", "1.10.0", "skills", "stale"), recursive: true);
        }

        var discovered = service.Discover(Request(temp));
        var result = service.Install(Request(temp), discovered, new SkillChoice(discovered.Skills));

        Assert.Empty(result.Removed);
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "current", "SKILL.md")));
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "stale", "SKILL.md")));
        Assert.Equal(2, InstallManifest.Load(result.Destination).EnumerateSkills().Count());
    }

    [Fact]
    public void Installed_skill_names_are_read_from_the_destination_manifest()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");

        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"))));
        service.Install(Request(temp));

        var installed = SkillInstallService.InstalledSkillNames(temp.Combine(".agents", "skills"));

        Assert.Contains("mockly", installed);
        // Destination names compare case-insensitively everywhere else, so they must here too.
        Assert.Contains("MOCKLY", installed);
    }

    [Fact]
    public void Installed_skill_names_are_empty_for_a_destination_that_does_not_exist_yet()
    {
        using var temp = new TempDirectory();

        Assert.Empty(SkillInstallService.InstalledSkillNames(temp.Combine("nowhere")));
    }

    [Fact]
    public void Uninstall_version_filter_leaves_another_version_installed()
    {
        using var temp = new TempDirectory();
        temp.CreatePackageWithSkill("Mockly", "1.11.0", "mockly");

        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json()));
        service.Install(Request(temp) with
        {
            Packages = [PackageCoordinate.Parse("Mockly@1.11.0")],
        });

        service.Uninstall(".agents/skills", temp.Path, "Mockly", "1.10.0", dryRun: false);

        Assert.True(Directory.Exists(temp.Combine(".agents", "skills", "mockly")));
    }

    [Fact]
    public void Uninstall_reverses_an_install()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");

        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"))));
        service.Install(Request(temp));

        var removed = service.Uninstall(".agents/skills", temp.Path, packageId: null, packageVersion: null, dryRun: false);

        Assert.Single(removed);
        Assert.False(Directory.Exists(temp.Combine(".agents", "skills")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Uninstall_stale_removes_skills_whose_package_left_the_target_or_changed_version(bool dryRun)
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        temp.CreatePackageWithSkill("Contoso.Widgets", "2.3.0", "widget-usage");
        temp.CreatePackageWithSkill("Alpha", "1.0.0", "alpha");
        new SkillInstallService(new FakeDotnet(
            temp.Combine("packages"),
            Json(("Mockly", "1.10.0"), ("Contoso.Widgets", "2.3.0"), ("Alpha", "1.0.0")))).Install(Request(temp));
        temp.CreateFile(".agents/skills/team-notes/SKILL.md", "ours");
        var destination = temp.Combine(".agents", "skills");
        var before = Snapshot(destination);
        // The cache is gone too: deciding what is stale needs only the references.
        Directory.Delete(temp.Combine("packages"), recursive: true);
        var runner = new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.11.0"), ("Alpha", "1.0.0")));
        var service = new SkillInstallService(runner);

        var references = service.ReadReferences(target: null, temp.Path, allowRestore: true);
        var removed = service.Uninstall(
            ".agents/skills", temp.Path, packageId: null, packageVersion: null, dryRun, staleAgainst: references.Packages);

        Assert.EndsWith("MyApp.sln", references.Target);
        Assert.Equal(
            [
                new TrackedSkill("mockly", "1.10.0", "mockly"),
                new TrackedSkill("contoso.widgets", "2.3.0", "widget-usage"),
            ],
            removed);
        Assert.DoesNotContain(runner.Invocations, line => line.Contains("locals", StringComparison.Ordinal));
        if (dryRun)
        {
            Assert.Equal(before, Snapshot(destination));
        }
        else
        {
            Assert.False(Directory.Exists(Path.Combine(destination, "mockly")));
            Assert.False(Directory.Exists(Path.Combine(destination, "widget-usage")));
            Assert.True(File.Exists(Path.Combine(destination, "alpha", "SKILL.md")));
            Assert.Equal("ours", File.ReadAllText(Path.Combine(destination, "team-notes", "SKILL.md")));
            Assert.Equal("alpha", Assert.Single(InstallManifest.Load(destination).Packages).Key);
        }
    }

    [Fact]
    public void Uninstall_stale_keeps_a_skill_whose_installed_version_is_still_referenced_beside_another()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly");
        new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json(("Mockly", "1.10.0"))))
            .Install(Request(temp));
        var service = new SkillInstallService(new FakeDotnet(
            temp.Combine("packages"), Json(("Mockly", "1.10.0"), ("Mockly", "1.11.0"))));

        var references = service.ReadReferences(target: null, temp.Path, allowRestore: true);
        var removed = service.Uninstall(
            ".agents/skills", temp.Path, null, null, dryRun: false, staleAgainst: references.Packages);

        Assert.Empty(removed);
        Assert.True(File.Exists(temp.Combine(".agents", "skills", "mockly", "SKILL.md")));
    }

    [Fact]
    public void Deciding_what_is_stale_requires_a_solution_or_project()
    {
        using var temp = new TempDirectory();
        var service = new SkillInstallService(new FakeDotnet(temp.Combine("packages"), Json()));

        var error = Assert.Throws<PackageSkillsException>(() =>
            service.ReadReferences(target: null, temp.Path, allowRestore: true));

        Assert.Contains("--target", error.Message);
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
}
