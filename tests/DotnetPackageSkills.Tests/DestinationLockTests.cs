using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class DestinationLockTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Install_and_uninstall_wait_for_the_destination_owner_to_finish(bool uninstall)
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        var package = temp.CreatePackageWithSkill("Alpha", "1.0.0", "shared");
        var skill = new BundledSkill("Alpha", "1.0.0", "shared", Path.Combine(package, "skills", "shared"), "shared");
        var installer = new SkillInstaller();
        installer.Install(destination, [skill], dryRun: false);
        var before = File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName));
        using var attempting = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using var held = DestinationLock.Acquire(destination);
        Exception? failure = null;
        var operation = new Thread(() =>
        {
            try
            {
                attempting.Set();
                if (uninstall)
                {
                    installer.Uninstall(destination, null, null, dryRun: false);
                }
                else
                {
                    // A version without the skill removes it, which empties the destination.
                    installer.Install(
                        destination,
                        [],
                        dryRun: false,
                        offered: new Dictionary<string, string> { ["Alpha"] = "2.0.0" });
                }
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                finished.Set();
            }
        }) { IsBackground = true };
        operation.Start();

        try
        {
            Assert.True(attempting.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(finished.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName)));
        }
        finally
        {
            held.Dispose();
            Assert.True(operation.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.Null(failure);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void A_busy_destination_returns_an_actionable_error_without_creating_files()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        using var held = DestinationLock.Acquire(destination);
        Exception? observed = null;
        var operation = new Thread(() =>
        {
            try
            {
                using var competing = DestinationLock.Acquire(destination, TimeSpan.Zero);
            }
            catch (Exception error)
            {
                observed = error;
            }
        }) { IsBackground = true };
        operation.Start();

        Assert.True(operation.Join(TimeSpan.FromSeconds(5)));
        var error = Assert.IsType<PackageSkillsException>(observed);

        Assert.Contains("Another operation", error.Message);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void Equivalent_destination_spellings_share_the_same_lock()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");

        Assert.Equal(DestinationLock.NameFor(destination),
            DestinationLock.NameFor(Path.Combine(destination, "..", "dest") + Path.DirectorySeparatorChar));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(DestinationLock.NameFor(destination), DestinationLock.NameFor(destination.ToUpperInvariant()));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extended_Windows_paths_share_the_ordinary_destination_lock(bool exists)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var destination = temp.Combine("nested", "skills");
        if (exists)
        {
            Directory.CreateDirectory(destination);
        }

        Assert.Equal(DestinationLock.NameFor(destination), DestinationLock.NameFor(@"\\?\" + destination));
        Assert.Equal(exists, Directory.Exists(destination));
    }

    [Fact]
    public void Existing_long_Windows_destinations_can_be_locked_with_either_spelling()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var destination = temp.Path;
        for (var index = 0; index < 5; index++)
        {
            destination = Path.Combine(destination, new string('a', 60));
        }

        Directory.CreateDirectory(destination);
        Assert.True(destination.Length > 260);
        Assert.Equal(DestinationLock.NameFor(destination), DestinationLock.NameFor(@"\\?\" + destination));
        using var held = DestinationLock.Acquire(destination);
    }
}
