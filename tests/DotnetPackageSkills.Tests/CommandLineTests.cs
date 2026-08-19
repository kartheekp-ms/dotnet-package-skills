using DotnetPackageSkills.Cli;

namespace DotnetPackageSkills.Tests;

public class CommandLineTests
{
    [Fact]
    public void Sync_rejects_the_removed_include_transitive_option()
    {
        var result = CommandLineBuilder.Build().Parse(["sync", "--include-transitive"]);

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Sync_rejects_interactive_combined_with_json()
    {
        var result = CommandLineBuilder.Build().Parse(["sync", "--interactive", "--json"]);

        Assert.Contains(result.Errors, error => error.Message.Contains("--interactive and --json"));
    }

    [Fact]
    public void Sync_accepts_the_short_interactive_alias()
    {
        Assert.Empty(CommandLineBuilder.Build().Parse(["sync", "-i"]).Errors);
    }

    [Fact]
    public void List_does_not_offer_interactive_selection()
    {
        // list writes nothing, so there is nothing to choose between.
        Assert.NotEmpty(CommandLineBuilder.Build().Parse(["list", "--interactive"]).Errors);
    }
}
