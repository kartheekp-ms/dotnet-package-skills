using DotnetPackageSkills.NuGet;

namespace DotnetPackageSkills.Tests;

public class PackagePathResolverTests
{
    [Theory]
    // Already normalized.
    [InlineData("1.2.3", "1.2.3")]
    // Padded to three parts.
    [InlineData("1.2", "1.2.0")]
    [InlineData("1", "1.0.0")]
    // A zero fourth part is dropped; a non-zero one is kept.
    [InlineData("1.2.3.0", "1.2.3")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    // Leading zeros are not part of the folder name.
    [InlineData("01.02.03", "1.2.3")]
    // Prerelease labels are preserved but lowercased.
    [InlineData("1.0.0-Beta.1", "1.0.0-beta.1")]
    [InlineData("2.0-RC1", "2.0.0-rc1")]
    // Build metadata is not part of package identity.
    [InlineData("1.2.3+build.99", "1.2.3")]
    [InlineData("1.2.3-alpha+sha.abc", "1.2.3-alpha")]
    [InlineData("  1.2.3  ", "1.2.3")]
    public void NormalizeVersion_matches_NuGet_folder_naming(string input, string expected) =>
        Assert.Equal(expected, PackagePathResolver.NormalizeVersion(input));

    [Fact]
    public void NormalizeVersion_leaves_unparseable_versions_alone_for_the_directory_scan() =>
        Assert.Equal("1.x.3", PackagePathResolver.NormalizeVersion("1.X.3"));

    [Fact]
    public void Resolve_finds_the_lowercased_folder_for_a_mixed_case_package_id()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("packages", "newtonsoft.json", "13.0.3");

        var resolved = PackagePathResolver.Resolve(temp.Combine("packages"), "Newtonsoft.Json", "13.0.3");

        Assert.Equal(temp.Combine("packages", "newtonsoft.json", "13.0.3"), resolved);
    }

    [Fact]
    public void Resolve_normalizes_the_version_before_looking()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("packages", "serilog", "4.1.0");

        Assert.NotNull(PackagePathResolver.Resolve(temp.Combine("packages"), "Serilog", "4.1"));
    }

    [Fact]
    public void Resolve_falls_back_to_scanning_when_normalization_does_not_match()
    {
        using var temp = new TempDirectory();

        // A folder name our rules would not produce, so only the scan can find it.
        temp.CreateDirectory("packages", "oddball", "1.2.3.4.5");

        Assert.NotNull(PackagePathResolver.Resolve(temp.Combine("packages"), "Oddball", "1.2.3.4.5"));
    }

    [Fact]
    public void Resolve_returns_null_when_the_package_is_not_extracted()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("packages");

        Assert.Null(PackagePathResolver.Resolve(temp.Combine("packages"), "Missing.Package", "1.0.0"));
    }

    [Fact]
    public void Resolve_returns_null_when_only_a_different_version_is_extracted()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("packages", "serilog", "4.1.0");

        Assert.Null(PackagePathResolver.Resolve(temp.Combine("packages"), "Serilog", "3.0.0"));
    }
}
