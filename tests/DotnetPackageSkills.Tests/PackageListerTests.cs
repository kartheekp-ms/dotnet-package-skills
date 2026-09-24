using DotnetPackageSkills.Infrastructure;
using DotnetPackageSkills.NuGet;

namespace DotnetPackageSkills.Tests;

public class PackageListerTests
{
    /// <summary>Records what was asked of the CLI and replays a canned result.</summary>
    private sealed class RecordingRunner(int exitCode, string standardOutput, string standardError = "")
        : IProcessRunner
    {
        public List<string> Invocations { get; } = [];

        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null)
        {
            Invocations.Add(string.Join(' ', arguments));
            return new ProcessResult(exitCode, standardOutput, standardError);
        }
    }

    private const string UnrestoredError =
        "No assets file was found for 'App.csproj'. Run restore before running this command.";

    /// <summary>What the .NET 10 SDK writes when its own restore fails during a JSON listing.</summary>
    private const string RestoreFailedJson = """
        {
          "version": 1,
          "problems": [
            { "text": "Restore failed. Run `dotnet restore` for more details on the issue.", "level": "error" }
          ]
        }
        """;

    [Fact]
    public void List_runs_dotnet_list_package_without_a_restore_option()
    {
        // Whether listing restores is the SDK's call: .NET 10 restores when it needs to, and
        // earlier SDKs say that the target has to be restored first.
        var runner = new RecordingRunner(0, TwoProjectsJson);

        new PackageLister(new DotnetCli(runner)).List("App.csproj");

        Assert.Equal("list App.csproj package --format json", Assert.Single(runner.Invocations));
    }

    [Fact]
    public void An_unrestored_target_is_reported_with_the_sdk_output_and_the_tool_never_restores_it()
    {
        // The customer restores and runs the command again; the tool doesn't restore for them.
        var runner = new RecordingRunner(1, string.Empty, UnrestoredError);

        var error = Assert.Throws<PackageSkillsException>(
            () => new PackageLister(new DotnetCli(runner)).List("App.csproj"));

        Assert.Contains("'dotnet list \"App.csproj\" package' failed with exit code 1", error.Message);
        Assert.Contains(UnrestoredError, error.Message);
        Assert.Contains("and then run this command again", error.Message);
        Assert.Equal("list App.csproj package --format json", Assert.Single(runner.Invocations));
    }

    [Fact]
    public void Problems_that_the_sdk_reports_in_json_are_shown_as_text()
    {
        var runner = new RecordingRunner(1, RestoreFailedJson);

        var error = Assert.Throws<PackageSkillsException>(
            () => new PackageLister(new DotnetCli(runner)).List("App.csproj"));

        Assert.Contains("error: Restore failed. Run `dotnet restore` for more details on the issue.", error.Message);
        Assert.DoesNotContain("\"problems\"", error.Message);
        Assert.Single(runner.Invocations);
    }

    private const string TwoProjectsJson = """
        {
          "version": 1,
          "parameters": "",
          "projects": [
            {
              "path": "/repo/src/Api/Api.csproj",
              "frameworks": [
                {
                  "framework": "net8.0",
                  "topLevelPackages": [
                    { "id": "Serilog", "requestedVersion": "4.1.0", "resolvedVersion": "4.1.0" },
                    { "id": "Mockly", "requestedVersion": "1.10.0", "resolvedVersion": "1.10.0" }
                  ],
                  "transitivePackages": [
                    { "id": "System.Text.Json", "resolvedVersion": "8.0.5" }
                  ]
                }
              ]
            },
            {
              "path": "/repo/src/Worker/Worker.csproj",
              "frameworks": [
                {
                  "framework": "net8.0",
                  "topLevelPackages": [
                    { "id": "Serilog", "requestedVersion": "4.1.0", "resolvedVersion": "4.1.0" }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Parse_returns_direct_packages_only()
    {
        var packages = PackageLister.Parse(TwoProjectsJson);

        Assert.Equal(["Mockly", "Serilog"], packages.Select(p => p.Id));
    }

    [Fact]
    public void Parse_deduplicates_a_package_referenced_by_several_projects()
    {
        var packages = PackageLister.Parse(TwoProjectsJson);

        Assert.Single(packages, package => package.Id == "Serilog");
    }

    [Fact]
    public void Parse_ignores_transitive_packages()
    {
        Assert.DoesNotContain(PackageLister.Parse(TwoProjectsJson), package => package.Id == "System.Text.Json");
    }

    [Fact]
    public void Parse_keeps_both_versions_when_frameworks_resolve_a_package_differently()
    {
        // Each version has its own folder in the global packages cache, so both matter.
        const string json = """
            {
              "projects": [
                {
                  "frameworks": [
                    { "framework": "net8.0", "topLevelPackages": [ { "id": "Widgets", "resolvedVersion": "1.0.0" } ] },
                    { "framework": "net10.0", "topLevelPackages": [ { "id": "Widgets", "resolvedVersion": "2.0.0" } ] }
                  ]
                }
              ]
            }
            """;

        var packages = PackageLister.Parse(json);

        Assert.Equal(["1.0.0", "2.0.0"], packages.Select(p => p.Version));
    }

    [Fact]
    public void Parse_prefers_the_resolved_version_over_the_requested_one()
    {
        // Central Package Management and floating versions leave a range in
        // requestedVersion; only resolvedVersion names a folder that exists.
        const string json = """
            {
              "projects": [
                {
                  "frameworks": [
                    {
                      "framework": "net8.0",
                      "topLevelPackages": [
                        { "id": "Widgets", "requestedVersion": "4.*", "resolvedVersion": "4.7.2" }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        Assert.Equal("4.7.2", PackageLister.Parse(json).Single().Version);
    }

    [Fact]
    public void Parse_handles_an_unrestored_project_with_no_frameworks_array()
    {
        const string json = """{ "version": 1, "projects": [ { "path": "/repo/src/Api/Api.csproj" } ] }""";

        Assert.Empty(PackageLister.Parse(json));
    }

    [Fact]
    public void Parse_handles_a_project_with_no_packages()
    {
        const string json = """
            { "projects": [ { "frameworks": [ { "framework": "net8.0", "topLevelPackages": [] } ] } ] }
            """;

        Assert.Empty(PackageLister.Parse(json));
    }

    [Fact]
    public void Parse_skips_MSBuild_noise_printed_before_the_payload()
    {
        var noisy = "warning NU1503: Skipping restore for project.\n" + TwoProjectsJson;

        Assert.NotEmpty(PackageLister.Parse(noisy));
    }

    [Fact]
    public void Parse_reports_unusable_output_as_actionable_guidance()
    {
        var exception = Assert.Throws<PackageSkillsException>(
            () => PackageLister.Parse("Unrecognized option '--format'"));

        Assert.Contains("7.0.200", exception.Message);
    }
}
