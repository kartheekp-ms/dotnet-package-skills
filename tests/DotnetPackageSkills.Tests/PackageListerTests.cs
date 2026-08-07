using DotnetPackageSkills.NuGet;

namespace DotnetPackageSkills.Tests;

public class PackageListerTests
{
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
