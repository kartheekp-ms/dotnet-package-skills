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
}
