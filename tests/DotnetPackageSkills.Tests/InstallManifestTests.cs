using System.Security.AccessControl;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class InstallManifestTests
{
    private const string Contents = """
        {"installed":[
          {"package":"Alpha","version":"1.0.0","skills":["alpha"]},
          {"package":"Beta","version":"1.0.0","skills":["beta"]}
        ]}
        """;

    [Fact]
    public void The_first_save_creates_an_ordinary_manifest_file()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        var path = Path.Combine(destination, InstallManifest.FileName);
        var manifest = new InstallManifest
        {
            Installed = [new ManifestEntry { Package = "New", Version = "2.0.0", Skills = ["new"] }],
        };

        manifest.Save(destination);

        Assert.True(File.Exists(path));
        Assert.Null(new FileInfo(path).LinkTarget);
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal("new", Assert.Single(InstallManifest.Load(destination).EnumerateSkills()).Skill);
        Assert.Equal(path, Assert.Single(Directory.EnumerateFileSystemEntries(destination)));
    }

    [Fact]
    public void Saving_a_regular_manifest_updates_it_without_creating_temporary_files()
    {
        using var temp = new TempDirectory();
        var destination = temp.CreateDirectory("dest");
        var path = temp.CreateFile("dest/.dotnet-package-skills.json", Contents);
        var manifest = InstallManifest.Load(destination);
        manifest.SetSkills([new TrackedSkill("New", "2.0.0", "new")]);

        manifest.Save(destination);

        Assert.Equal("new", Assert.Single(InstallManifest.Load(destination).EnumerateSkills()).Skill);
        Assert.Equal(path, Assert.Single(Directory.EnumerateFileSystemEntries(destination)));
        Assert.Null(new FileInfo(path).LinkTarget);
    }

    [Fact]
    public void A_read_only_manifest_is_not_silently_overwritten()
    {
        using var temp = new TempDirectory();
        var destination = temp.CreateDirectory("dest");
        var path = temp.CreateFile("dest/.dotnet-package-skills.json", Contents);
        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => new InstallManifest().Save(destination));
            Assert.Equal(Contents, File.ReadAllText(path));
            Assert.Equal(path, Assert.Single(Directory.EnumerateFileSystemEntries(destination)));
        }
        finally
        {
            File.SetAttributes(path, attributes);
        }
    }

    [Fact]
    public void Saving_a_manifest_preserves_its_access_permissions()
    {
        using var temp = new TempDirectory();
        var destination = temp.CreateDirectory("dest");
        var path = temp.CreateFile("dest/.dotnet-package-skills.json", Contents);
        if (OperatingSystem.IsWindows())
        {
            var file = new FileInfo(path);
            var access = file.GetAccessControl(AccessControlSections.Access);
            access.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            file.SetAccessControl(access);
            var before = file.GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

            InstallManifest.Load(destination).Save(destination);

            Assert.Equal(before, new FileInfo(path).GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        else
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, mode);

            InstallManifest.Load(destination).Save(destination);

            Assert.Equal(mode, File.GetUnixFileMode(path));
        }
    }
}
