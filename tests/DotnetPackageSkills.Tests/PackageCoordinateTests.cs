using DotnetPackageSkills.NuGet;

namespace DotnetPackageSkills.Tests;

public class PackageCoordinateTests
{
    [Theory]
    [InlineData("Mockly@1.10.0", "Mockly", "1.10.0")]
    [InlineData("Contoso.Widgets@2.3.0", "Contoso.Widgets", "2.3.0")]
    [InlineData("My_Package-Name@1.0.0-beta.1", "My_Package-Name", "1.0.0-beta.1")]
    [InlineData("Widgets@2.0", "Widgets", "2.0")]
    [InlineData("Widgets@1.2.3.4", "Widgets", "1.2.3.4")]
    [InlineData("Widgets@1.2.3+sha.abc", "Widgets", "1.2.3+sha.abc")]
    [InlineData("  Mockly@1.10.0  ", "Mockly", "1.10.0")]
    public void Parse_accepts_an_exact_package_and_version(string input, string id, string version)
    {
        var coordinate = PackageCoordinate.Parse(input);

        Assert.Equal(id, coordinate.Id);
        Assert.Equal(version, coordinate.Version);
    }

    [Theory]
    [InlineData("Mockly@4.*")]
    [InlineData("Mockly@*")]
    [InlineData("Mockly@1.2.*")]
    [InlineData("Mockly@[1.0,2.0)")]
    [InlineData("Mockly@(,3.0]")]
    [InlineData("Mockly@[1.0]")]
    public void Parse_refuses_floating_versions_and_ranges(string input)
    {
        var exception = Assert.Throws<PackageSkillsException>(() => PackageCoordinate.Parse(input));

        // Guessing a version would copy skills describing a release the user does not use,
        // so the message has to point at the option that resolves versions properly.
        Assert.Contains("exact version", exception.Message);
        Assert.Contains("--target", exception.Message);
    }

    [Fact]
    public void Parse_tells_the_user_how_to_add_a_missing_version()
    {
        var exception = Assert.Throws<PackageSkillsException>(() => PackageCoordinate.Parse("Mockly"));

        Assert.Contains("Mockly@1.10.0", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@1.0.0")]
    [InlineData("Mockly@")]
    [InlineData("Mockly@@1.0.0")]
    [InlineData("Mockly@not-a-version")]
    [InlineData("Mockly@v1.0.0")]
    [InlineData("../evil@1.0.0")]
    [InlineData("path/to/thing@1.0.0")]
    public void Parse_rejects_malformed_input(string input) =>
        Assert.Throws<PackageSkillsException>(() => PackageCoordinate.Parse(input));

    [Fact]
    public void ToString_round_trips() =>
        Assert.Equal("Mockly@1.10.0", PackageCoordinate.Parse("Mockly@1.10.0").ToString());
}
