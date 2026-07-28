using DotnetPackageSkills.NuGet;

namespace DotnetPackageSkills.Tests;

public class GlobalPackagesLocatorTests
{
    private static string SomeAbsolutePath => Path.Combine(Path.GetTempPath(), "nuget-packages");

    [Fact]
    public void ParseListOutput_reads_the_path_from_current_SDK_output()
    {
        var output = $"global-packages: {SomeAbsolutePath}";

        Assert.Equal(Path.GetFullPath(SomeAbsolutePath), GlobalPackagesLocator.ParseListOutput(output));
    }

    [Fact]
    public void ParseListOutput_reads_the_path_from_older_prefixed_output()
    {
        // Older SDKs prefix the line, which is why parsing keys off the label.
        var output = $"info : global-packages: {SomeAbsolutePath}";

        Assert.Equal(Path.GetFullPath(SomeAbsolutePath), GlobalPackagesLocator.ParseListOutput(output));
    }

    [Fact]
    public void ParseListOutput_ignores_surrounding_lines()
    {
        var output = $"""
            Welcome to .NET!
            ----------------
            global-packages: {SomeAbsolutePath}

            """;

        Assert.Equal(Path.GetFullPath(SomeAbsolutePath), GlobalPackagesLocator.ParseListOutput(output));
    }

    [Fact]
    public void ParseListOutput_tolerates_windows_line_endings()
    {
        var output = $"info : something\r\nglobal-packages: {SomeAbsolutePath}\r\n";

        Assert.Equal(Path.GetFullPath(SomeAbsolutePath), GlobalPackagesLocator.ParseListOutput(output));
    }

    [Fact]
    public void ParseListOutput_returns_null_when_the_label_is_absent() =>
        Assert.Null(GlobalPackagesLocator.ParseListOutput("http-cache: /somewhere\ntemp: /elsewhere"));

    [Fact]
    public void ParseListOutput_returns_null_when_the_label_has_no_value() =>
        Assert.Null(GlobalPackagesLocator.ParseListOutput("global-packages:   "));
}
