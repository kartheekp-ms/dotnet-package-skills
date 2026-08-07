using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class SkillDiscoveryTests
{
    [Fact]
    public void A_package_with_one_skill_uses_its_authored_folder_name_as_the_destination()
    {
        using var temp = new TempDirectory();
        var package = temp.CreatePackageWithSkill("Mockly", "1.10.0", "mockly-usage");

        var skill = Assert.Single(SkillDiscovery.Discover(package, "Mockly", "1.10.0"));

        Assert.Equal("mockly-usage", skill.RelativePath);
    }

    [Fact]
    public void A_package_with_several_skills_keeps_each_authored_folder_name()
    {
        using var temp = new TempDirectory();
        var package = temp.CreatePackageWithSkill("Contoso.Widgets", "2.3.0", "widget-usage", "widget-testing");

        var skills = SkillDiscovery.Discover(package, "Contoso.Widgets", "2.3.0");

        Assert.Equal(
            ["widget-testing", "widget-usage"],
            skills.Select(s => s.RelativePath));
    }

    [Fact]
    public void Discover_finds_each_subdirectory_of_the_skills_folder()
    {
        using var temp = new TempDirectory();
        var package = temp.CreatePackageWithSkill("Contoso.Widgets", "2.3.0", "widget-usage", "widget-testing");

        var skills = SkillDiscovery.Discover(package, "Contoso.Widgets", "2.3.0");

        Assert.Equal(["widget-testing", "widget-usage"], skills.Select(s => s.SkillName));
    }

    [Fact]
    public void Discover_keeps_package_version_as_metadata_only()
    {
        using var temp = new TempDirectory();
        var package = temp.CreatePackageWithSkill("Widgets", "2.0", "usage");

        var skill = Assert.Single(SkillDiscovery.Discover(package, "Widgets", "2.0"));

        Assert.Equal("2.0", skill.PackageVersion);
        Assert.Equal("usage", skill.RelativePath);
    }

    [Fact]
    public void Discover_returns_nothing_when_the_package_ships_no_skills_folder()
    {
        using var temp = new TempDirectory();
        var package = temp.CreateDirectory("packages", "newtonsoft.json", "13.0.3");
        temp.CreateFile("packages/newtonsoft.json/13.0.3/lib/net8.0/Newtonsoft.Json.dll");

        Assert.Empty(SkillDiscovery.Discover(package, "Newtonsoft.Json", "13.0.3"));
    }

    [Fact]
    public void Discover_ignores_a_skill_placed_directly_in_the_skills_folder()
    {
        using var temp = new TempDirectory();
        var package = temp.CreateDirectory("packages", "widgets", "1.0.0");
        temp.CreateFile("packages/widgets/1.0.0/skills/SKILL.md", "---\nname: widgets\n---\n");

        Assert.Empty(SkillDiscovery.Discover(package, "Widgets", "1.0.0"));
    }

    [Fact]
    public void Discover_ignores_subdirectories_without_a_skill_manifest()
    {
        using var temp = new TempDirectory();
        var package = temp.CreateDirectory("packages", "widgets", "1.0.0");
        temp.CreateFile("packages/widgets/1.0.0/skills/notes/readme.md", "not a skill");

        Assert.Empty(SkillDiscovery.Discover(package, "Widgets", "1.0.0"));
    }

    [Fact]
    public void Discover_matches_the_skills_folder_regardless_of_casing()
    {
        using var temp = new TempDirectory();
        var package = temp.CreateDirectory("packages", "widgets", "1.0.0");
        temp.CreateFile("packages/widgets/1.0.0/Skills/usage/SKILL.md", "---\n---\n");

        Assert.Single(SkillDiscovery.Discover(package, "Widgets", "1.0.0"));
    }

    [Fact]
    public void Discover_returns_nothing_for_a_package_that_is_not_on_disk()
    {
        using var temp = new TempDirectory();

        Assert.Empty(SkillDiscovery.Discover(temp.Combine("nope"), "Ghost", "1.0.0"));
    }

    [Theory]
    [InlineData("widget-usage", true)]
    [InlineData("Widget.Usage_2", true)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    public void Skill_names_that_could_escape_the_destination_are_rejected(string name, bool expected) =>
        Assert.Equal(expected, SkillDiscovery.IsSafeSkillName(name));
}
