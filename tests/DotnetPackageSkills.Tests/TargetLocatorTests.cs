using DotnetPackageSkills.NuGet;

namespace DotnetPackageSkills.Tests;

public class TargetLocatorTests
{
    [Fact]
    public void Detect_prefers_a_solution_over_a_project()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreateFile("MyApp.csproj");

        Assert.EndsWith("MyApp.sln", TargetLocator.Detect(temp.Path));
    }

    [Fact]
    public void Detect_prefers_slnx_over_sln()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("MyApp.sln");
        temp.CreateFile("MyApp.slnx");

        Assert.EndsWith("MyApp.slnx", TargetLocator.Detect(temp.Path));
    }

    [Fact]
    public void Detect_prefers_the_top_level_over_a_nested_solution()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Root.sln");
        temp.CreateFile("nested/Inner.sln");

        Assert.EndsWith("Root.sln", TargetLocator.Detect(temp.Path));
    }

    [Fact]
    public void Detect_descends_when_nothing_is_at_the_top_level()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src/MyApp/MyApp.csproj");

        Assert.EndsWith("MyApp.csproj", TargetLocator.Detect(temp.Path));
    }

    [Fact]
    public void Detect_ignores_build_output_directories()
    {
        using var temp = new TempDirectory();

        // Project files copied into obj/ during restore would otherwise win by sort order.
        temp.CreateFile("obj/Aaa.csproj");
        temp.CreateFile("src/Real.csproj");

        Assert.EndsWith("Real.csproj", TargetLocator.Detect(temp.Path));
    }

    [Fact]
    public void Detect_finds_fsproj_and_vbproj_too()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src/MyApp.fsproj");

        Assert.EndsWith("MyApp.fsproj", TargetLocator.Detect(temp.Path));
    }

    [Fact]
    public void Detect_explains_what_to_do_when_there_is_no_target()
    {
        using var temp = new TempDirectory();

        var exception = Assert.Throws<PackageSkillsException>(() => TargetLocator.Detect(temp.Path));

        Assert.Contains("--target", exception.Message);
    }

    [Fact]
    public void Resolve_accepts_a_path_relative_to_the_working_directory()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src/MyApp/MyApp.csproj");

        var resolved = TargetLocator.Resolve("src/MyApp/MyApp.csproj", temp.Path);

        Assert.True(Path.IsPathRooted(resolved));
        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void Resolve_searches_within_a_directory_that_was_passed_as_the_target()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("src/MyApp/MyApp.csproj");

        Assert.EndsWith("MyApp.csproj", TargetLocator.Resolve("src", temp.Path));
    }

    [Fact]
    public void Resolve_rejects_a_file_that_is_not_a_project_or_solution()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("notes.txt");

        var exception = Assert.Throws<PackageSkillsException>(() => TargetLocator.Resolve("notes.txt", temp.Path));

        Assert.Contains(".csproj", exception.Message);
    }

    [Fact]
    public void Resolve_reports_a_missing_target_by_full_path()
    {
        using var temp = new TempDirectory();

        var exception = Assert.Throws<PackageSkillsException>(() => TargetLocator.Resolve("Ghost.sln", temp.Path));

        Assert.Contains("Ghost.sln", exception.Message);
    }
}
