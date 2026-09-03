using DotnetPackageSkills.Cli;

namespace DotnetPackageSkills.Tests;

public class CommandLineTests
{
    [Fact]
    public void Uninstall_says_it_removes_from_the_destination_rather_than_copying_into_it()
    {
        var uninstall = CommandLineBuilder.Build()
            .Subcommands.Single(command => command.Name == "uninstall");

        var destination = uninstall.Options.Single(option => option.Name == "--destination");

        // The option is shared-looking but not shared: install's wording is about copying in,
        // which reads as nonsense on a command that only deletes.
        Assert.Contains("remove skills from", destination.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("copy", destination.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Uninstall_still_accepts_a_destination()
    {
        // Skills installed anywhere but the default are unreachable without it.
        Assert.Empty(CommandLineBuilder.Build().Parse(["uninstall", "-d", ".claude/skills"]).Errors);
    }

    [Fact]
    public void Install_still_says_it_copies_into_the_destination()
    {
        var install = CommandLineBuilder.Build()
            .Subcommands.Single(command => command.Name == "install");

        var destination = install.Options.Single(option => option.Name == "--destination");

        Assert.Contains("copy skills into", destination.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_removed_sync_verb_is_rejected()
    {
        // Renamed to install, which pairs with uninstall. A clean break at 0.1.0 rather than
        // an alias, so nothing has to carry the old name forward.
        var result = CommandLineBuilder.Build().Parse(["sync"]);

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Install_rejects_the_removed_include_transitive_option()
    {
        var result = CommandLineBuilder.Build().Parse(["install", "--include-transitive"]);

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Install_rejects_interactive_combined_with_json()
    {
        var result = CommandLineBuilder.Build().Parse(["install", "--interactive", "--json"]);

        Assert.Contains(result.Errors, error => error.Message.Contains("--interactive and --json"));
    }

    [Fact]
    public void Install_accepts_the_short_interactive_alias()
    {
        Assert.Empty(CommandLineBuilder.Build().Parse(["install", "-i"]).Errors);
    }

    [Fact]
    public void Install_accepts_interactive_alongside_a_named_package()
    {
        // One package can ship a dozen skills, so choosing among them is exactly the case
        // --package plus --interactive exists for.
        Assert.Empty(CommandLineBuilder.Build().Parse(["install", "--package", "Mockly@1.10.0", "-i"]).Errors);
        Assert.Empty(CommandLineBuilder.Build().Parse(["install", "-i", "--package", "Mockly@1.10.0"]).Errors);
    }

    [Fact]
    public void List_does_not_offer_interactive_selection()
    {
        // list writes nothing, so there is nothing to choose between.
        Assert.NotEmpty(CommandLineBuilder.Build().Parse(["list", "--interactive"]).Errors);
    }
}
