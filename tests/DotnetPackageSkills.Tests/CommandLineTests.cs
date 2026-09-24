using System.CommandLine;
using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class CommandLineTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void A_supplied_blank_uninstall_filter_is_rejected(string filter)
    {
        var error = Assert.Throws<PackageSkillsException>(() => CommandLineBuilder.ParseUninstallFilter(filter));

        Assert.Contains("non-empty package ID", error.Message);
        Assert.NotEmpty(CommandLineBuilder.Build().Parse(["uninstall", "--package", filter]).Errors);
    }

    [Theory]
    [InlineData("--package")]
    [InlineData("-p")]
    public void An_uninstall_package_option_without_a_value_is_rejected(string option)
    {
        Assert.NotEmpty(CommandLineBuilder.Build().Parse(["uninstall", option]).Errors);
        Assert.NotEmpty(CommandLineBuilder.Build().Parse(["uninstall", option, "--dry-run"]).Errors);
    }

    [Fact]
    public void Only_an_absent_uninstall_filter_means_all_packages()
    {
        Assert.Equal((null, null), CommandLineBuilder.ParseUninstallFilter(null));
        Assert.Equal(("Mockly", null), CommandLineBuilder.ParseUninstallFilter(" Mockly "));
    }

    [Theory]
    [InlineData("--package", "only once")]
    [InlineData("-p", "only once")]
    [InlineData("--package=", "only once")]
    [InlineData("--package=Alpha", "expects a single argument")]
    public void Repeated_uninstall_filters_are_rejected_even_when_the_last_value_is_missing(
        string repeated, string message)
    {
        var result = CommandLineBuilder.Build().Parse(
            ["uninstall", "--dry-run", "--package", "Alpha", repeated]);

        Assert.Contains(result.Errors, error => error.Message.Contains(message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("_Acme")]
    [InlineData("Acme_")]
    [InlineData("_")]
    public void Uninstall_accepts_valid_underscore_boundary_package_ids(string id)
    {
        Assert.Equal((id, null), CommandLineBuilder.ParseUninstallFilter(id));
        Assert.Empty(CommandLineBuilder.Build().Parse(["uninstall", "--package", id, "--dry-run"]).Errors);
    }

    [Theory]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2.0.0", "1.2.0")]
    [InlineData("1.2.0-RC.1", "1.2.0-rc.1")]
    public void Uninstall_filter_matching_normalizes_versions_for_both_modes(string filterVersion, string installedVersion)
    {
        var (id, version) = CommandLineBuilder.ParseUninstallFilter($"mockly@{filterVersion}");

        Assert.True(SkillInstaller.Matches(new TrackedSkill("Mockly", installedVersion, "usage"), id, version));
        Assert.False(SkillInstaller.Matches(new TrackedSkill("Other", installedVersion, "usage"), id, version));
    }

    [Fact]
    public void Uninstall_accepts_the_interactive_flag()
    {
        Assert.Empty(CommandLineBuilder.Build().Parse(["uninstall", "-i"]).Errors);
        Assert.Empty(CommandLineBuilder.Build().Parse(["uninstall", "--interactive"]).Errors);
    }

    [Fact]
    public void Uninstall_stale_accepts_a_target_and_every_other_uninstall_option_but_a_package()
    {
        Assert.Empty(CommandLineBuilder.Build().Parse(
            ["uninstall", "--stale", "--target", "App.sln", "--dry-run", "-i", "-d", ".claude/skills"])
            .Errors);
        Assert.Empty(CommandLineBuilder.Build().Parse(["uninstall", "--stale", "-t", "src"]).Errors);
        Assert.Empty(CommandLineBuilder.Build().Parse(["uninstall", "--stale"]).Errors);
    }

    [Fact]
    public void Uninstall_stale_cannot_be_combined_with_a_package_filter()
    {
        var result = CommandLineBuilder.Build().Parse(["uninstall", "--stale", "--package", "Mockly"]);

        Assert.Contains(result.Errors, error => error.Message.Contains("--stale and --package cannot be combined"));
    }

    [Theory]
    [InlineData("--target")]
    [InlineData("-t")]
    public void Uninstall_target_needs_stale(string option)
    {
        var result = CommandLineBuilder.Build().Parse(["uninstall", option, "App.sln"]);

        Assert.Contains(result.Errors, error =>
            error.Message.Contains("--target can be used with uninstall only together with --stale", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("install", null)]
    [InlineData("list", null)]
    [InlineData("uninstall", "--stale")]
    public void No_command_offers_no_restore(string command, string? extra)
    {
        // Restoring is left to dotnet list package and to the customer, so there is nothing to
        // turn off here.
        Assert.DoesNotContain(
            CommandLineBuilder.Build().Subcommands.Single(candidate => candidate.Name == command).Options,
            option => option.Name == "--no-restore");
        string[] args = extra is null ? [command, "--no-restore"] : [command, extra, "--no-restore"];
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke(args, output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unrecognized command or argument '--no-restore'", error.ToString());
    }

    [Theory]
    [InlineData("install")]
    [InlineData("list")]
    public void Only_uninstall_has_the_stale_option(string command)
    {
        Assert.NotEmpty(CommandLineBuilder.Build().Parse([command, "--stale"]).Errors);
    }

    [Fact]
    public void The_interactive_install_help_says_installed_skills_are_not_listed()
    {
        var install = CommandLineBuilder.Build().Subcommands.Single(command => command.Name == "install");

        var interactive = install.Options.Single(option => option.Name == "--interactive");

        Assert.Contains("aren't installed", interactive.Description);
        Assert.DoesNotContain("remove", interactive.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("list")]
    [InlineData("uninstall")]
    public void No_command_offers_json_output(string command)
    {
        // Reports are for people. The manifest is the only machine-readable output.
        Assert.DoesNotContain(
            CommandLineBuilder.Build().Subcommands.Single(candidate => candidate.Name == command).Options,
            option => option.Name == "--json");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke([command, "--json"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unrecognized command or argument '--json'", error.ToString());
    }

    [Theory]
    [InlineData("install")]
    [InlineData("list")]
    public void An_unknown_option_after_package_values_is_reported_as_unrecognized(string command)
    {
        // --package takes several values, so the parser hands it a trailing unknown option, such
        // as a --json left in an old script, as one more value.
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke(
            [command, "--package", "Mockly@1.10.0", "--json"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unrecognized command or argument '--json'", error.ToString());
        Assert.DoesNotContain("missing a version", error.ToString());
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_uninstall_filters_are_rejected_without_printing_terminal_controls(bool interactive)
    {
        const string Filter = "Safe\u001b]52;c;SECRET\aPackage";
        string[] args = interactive
            ? ["uninstall", "--interactive", "--package", Filter]
            : ["uninstall", "--package", Filter];
        var parsed = CommandLineBuilder.Build().Parse(args);
        Assert.Contains(parsed.Errors, error => error.Message.Contains(Filter, StringComparison.Ordinal));
        Assert.Equal(Filter, parsed.GetValue<string>("--package"));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke(args, output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("'SafePackage' is not a valid package id.", error.ToString());
        Assert.DoesNotContain('\u001b', error.ToString());
        Assert.DoesNotContain('\a', error.ToString());
        Assert.DoesNotContain("SECRET", error.ToString());
        Assert.Contains("Usage:", output.ToString());
    }

    [Fact]
    public void Uninstall_validation_keeps_multiline_guidance_readable()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke(
            ["uninstall", "--package", "Mockly@1.*\u001b]52;c;SECRET\a"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains(
            "'1.*' is a floating version or a version range, and this tool needs an exact version.\n" +
            "Write it out, for example --package Mockly@1.10.0.\n" +
            "To let restore choose the version, point at a project or solution with --target instead.",
            error.ToString().ReplaceLineEndings("\n"));
        Assert.DoesNotContain('\u001b', error.ToString());
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    [Theory]
    [InlineData("--unknown\u001b]52;c;SECRET\a")]
    [InlineData("--unknown\u009d52;c;SECRET\u009c")]
    [InlineData("--dry-run=false\u001b]52;c;SECRET\u001b\\")]
    public void Framework_argument_errors_do_not_emit_terminal_controls(string token)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke(["uninstall", token], output, error);

        Assert.Equal(1, exitCode);
        Assert.NotEmpty(error.ToString());
        Assert.DoesNotContain("SECRET", output.ToString() + error.ToString());
        Assert.DoesNotContain(output.ToString() + error.ToString(),
            character => char.IsControl(character) && character is not ('\r' or '\n'));
    }

    [Fact]
    public void Framework_typo_suggestions_are_sanitized_on_standard_output_too()
    {
        const string Token = "uninstal\u001b";
        var parsed = CommandLineBuilder.Build().Parse([Token]);
        Assert.Contains(Token, parsed.UnmatchedTokens);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke([Token], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("Did you mean", output.ToString());
        Assert.Contains("uninstall", output.ToString());
        Assert.Contains("Unrecognized", error.ToString());
        Assert.DoesNotContain('\u001b', output.ToString() + error.ToString());
    }

    [Theory]
    [InlineData("--help", null)]
    [InlineData("--version", null)]
    [InlineData("uninstall", "--help")]
    [InlineData("uninstall", "--missing")]
    [InlineData("uninstal", null)]
    public void Ordinary_framework_output_and_exit_codes_are_unchanged(string first, string? second)
    {
        string[] args = second is null ? [first] : [first, second];
        using var expectedOutput = new StringWriter();
        using var expectedError = new StringWriter();
        var expectedExitCode = CommandLineBuilder.Build().Parse(args).Invoke(new InvocationConfiguration
        {
            Output = expectedOutput,
            Error = expectedError,
        });
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CommandLineBuilder.Invoke(args, output, error);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(expectedOutput.ToString(), output.ToString());
        Assert.Equal(expectedError.ToString(), error.ToString());
    }
}
