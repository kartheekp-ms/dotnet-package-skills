using System.Security.AccessControl;
using System.Text;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Tests;

public class InstallManifestTests
{
    private const string Contents = """
        {
          "version": 1,
          "packages": {
            "alpha": { "version": "1.0.0", "skills": ["alpha"] },
            "beta": { "version": "1.0.0", "skills": ["beta"] }
          }
        }
        """;

    [Fact]
    public void Saving_writes_the_versioned_packages_format_with_lf_line_endings_only()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        var manifest = new InstallManifest();
        manifest.SetSkills(
        [
            new TrackedSkill("Mockly", "1.10", "mockly-usage"),
            new TrackedSkill("Mockly", "1.10.0", "mockly-migration"),
            new TrackedSkill("Contoso.Widgets", "2.3.0", "contoso.widgets-usage"),
        ]);

        manifest.Save(destination);

        var bytes = File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName));
        Assert.Equal(
            "{\n" +
            "  \"version\": 1,\n" +
            "  \"packages\": {\n" +
            "    \"contoso.widgets\": {\n" +
            "      \"version\": \"2.3.0\",\n" +
            "      \"skills\": [\n" +
            "        \"contoso.widgets-usage\"\n" +
            "      ]\n" +
            "    },\n" +
            "    \"mockly\": {\n" +
            "      \"version\": \"1.10.0\",\n" +
            "      \"skills\": [\n" +
            "        \"mockly-migration\",\n" +
            "        \"mockly-usage\"\n" +
            "      ]\n" +
            "    }\n" +
            "  }\n" +
            "}\n",
            Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.NotEqual(0xEF, bytes[0]);
    }

    [Fact]
    public void The_same_skills_produce_the_same_bytes_whatever_their_order_or_casing()
    {
        using var temp = new TempDirectory();
        var first = new InstallManifest();
        first.SetSkills(
        [
            new TrackedSkill("Mockly", "1.0.0-RC.1", "b"),
            new TrackedSkill("Alpha", "2.0", "a"),
            new TrackedSkill("mockly", "1.0.0-rc.1", "a"),
        ]);
        var second = new InstallManifest();
        second.SetSkills(
        [
            new TrackedSkill("alpha", "2.0.0", "a"),
            new TrackedSkill("MOCKLY", "1.0.0-rc.1", "a"),
            new TrackedSkill("Mockly", "1.0.0-RC.1", "b"),
        ]);

        first.Save(temp.Combine("first"));
        second.Save(temp.Combine("second"));

        Assert.Equal(
            File.ReadAllBytes(temp.Combine("first", InstallManifest.FileName)),
            File.ReadAllBytes(temp.Combine("second", InstallManifest.FileName)));
    }

    [Fact]
    public void A_saved_manifest_reads_back_with_lowercase_ids_and_normalized_versions()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        var manifest = new InstallManifest();
        manifest.SetSkills(
        [
            new TrackedSkill("Mockly", "1.10", "mockly-usage"),
            new TrackedSkill("Contoso.Widgets", "2.3.0", "contoso.widgets-usage"),
        ]);
        manifest.Save(destination);

        var loaded = InstallManifest.Load(destination);

        Assert.Equal(
            [
                new TrackedSkill("contoso.widgets", "2.3.0", "contoso.widgets-usage"),
                new TrackedSkill("mockly", "1.10.0", "mockly-usage"),
            ],
            loaded.EnumerateSkills());
        Assert.Equal(["contoso.widgets", "mockly"], loaded.Packages.Keys);
        Assert.Equal("1.10.0", loaded.Packages["mockly"].Version);
    }

    [Fact]
    public void One_package_cannot_be_recorded_at_two_versions()
    {
        var manifest = new InstallManifest();

        var error = Assert.Throws<PackageSkillsException>(() => manifest.SetSkills(
        [
            new TrackedSkill("Mockly", "1.10.0", "mockly-usage"),
            new TrackedSkill("mockly", "1.11.0", "mockly-testing"),
        ]));

        Assert.Contains("mockly", error.Message);
        Assert.Contains("1.10.0", error.Message);
        Assert.Contains("1.11.0", error.Message);
        Assert.Contains("No skills were changed", error.Message);
        Assert.Empty(manifest.EnumerateSkills());
    }

    [Fact]
    public void A_package_id_that_the_reader_would_refuse_is_never_written()
    {
        // Whatever the tool writes, it has to be able to read back. Otherwise one install would
        // lock every later command out of the destination.
        var manifest = new InstallManifest();

        var error = Assert.Throws<PackageSkillsException>(() => manifest.SetSkills(
            [new TrackedSkill("not valid!", "1.0.0", "a")]));

        Assert.Contains("'not valid!'", error.Message);
        Assert.Contains("not a valid package id", error.Message);
        Assert.Contains("No skills were changed", error.Message);
        Assert.Empty(manifest.EnumerateSkills());
    }

    [Fact]
    public void Property_names_and_package_ids_are_read_without_regard_to_case()
    {
        using var temp = new TempDirectory();
        var destination = temp.CreateDirectory("dest");
        temp.CreateFile(
            "dest/.dotnet-package-skills.json",
            """{"Version":1,"Packages":{"Mockly":{"Version":"1.10.0","Skills":["mockly-usage"]}}}""");

        Assert.Equal(
            new TrackedSkill("mockly", "1.10.0", "mockly-usage"),
            Assert.Single(InstallManifest.Load(destination).EnumerateSkills()));
    }

    [Fact]
    public void Unknown_properties_are_ignored_and_an_empty_packages_map_tracks_nothing()
    {
        using var temp = new TempDirectory();
        var withExtras = temp.CreateDirectory("extras");
        var empty = temp.CreateDirectory("empty");
        temp.CreateFile(
            "extras/.dotnet-package-skills.json",
            """
            {
              "version": 1,
              "comment": "not part of the format",
              "packages": { "mockly": { "version": "1.10.0", "skills": ["mockly-usage"], "extra": true } }
            }
            """);
        temp.CreateFile("empty/.dotnet-package-skills.json", """{"version":1,"packages":{}}""");

        Assert.Equal("mockly-usage", Assert.Single(InstallManifest.Load(withExtras).EnumerateSkills()).Skill);
        Assert.Empty(InstallManifest.Load(empty).EnumerateSkills());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public void A_manifest_from_a_newer_tool_asks_for_an_update_and_is_preserved(int formatVersion)
    {
        using var temp = new TempDirectory();
        var destination = temp.CreateDirectory("dest");
        var contents =
            "{\"version\":" + formatVersion +
            ",\"packages\":{\"mockly\":{\"version\":\"1.10.0\",\"skills\":[\"mockly-usage\"]}}}";
        var path = temp.CreateFile("dest/.dotnet-package-skills.json", contents);

        var error = Assert.Throws<PackageSkillsException>(() => InstallManifest.Load(destination));

        Assert.Contains($"format version {formatVersion}", error.Message);
        Assert.Contains("supports only version 1", error.Message);
        Assert.Contains("Update dotnet-package-skills", error.Message);
        Assert.Contains("No skills were changed", error.Message);
        Assert.Equal(contents, File.ReadAllText(path));
    }

    [Fact]
    public void A_manifest_in_the_pre_release_format_says_how_to_start_over_and_is_preserved()
    {
        using var temp = new TempDirectory();
        var destination = temp.CreateDirectory("dest");
        const string PreRelease = """
            {
              "note": "Written by the dotnet-package-skills tool.",
              "installed": [ { "package": "Mockly", "version": "1.10.0", "skills": ["mockly-usage"] } ]
            }
            """;
        var path = temp.CreateFile("dest/.dotnet-package-skills.json", PreRelease);

        var error = Assert.Throws<PackageSkillsException>(() => InstallManifest.Load(destination));

        Assert.Contains("pre-release version of dotnet-package-skills", error.Message);
        Assert.Contains("Move the skills folder aside", error.Message);
        Assert.Contains("No skills were changed", error.Message);
        Assert.Equal(PreRelease, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"packages":{}}""")]
    [InlineData("""{"version":"1","packages":{}}""")]
    [InlineData("""{"version":1.5,"packages":{}}""")]
    [InlineData("""{"version":0,"packages":{}}""")]
    [InlineData("""{"version":-1,"packages":{}}""")]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"version":1,"packages":null}""")]
    [InlineData("""{"version":1,"packages":[]}""")]
    [InlineData("""{"version":1,"version":1,"packages":{}}""")]
    [InlineData("""{"version":1,"packages":{},"Packages":{}}""")]
    [InlineData("""{"version":1,"packages":{"mockly":{"version":"1.0.0","skills":["a"]},"Mockly":{"version":"1.0.0","skills":["b"]}}}""")]
    [InlineData("""{"version":1,"packages":{"not valid!":{"version":"1.0.0","skills":["a"]}}}""")]
    [InlineData("""{"version":1,"packages":{"contoso..widgets":{"version":"1.0.0","skills":["a"]}}}""")]
    [InlineData("""{"version":1,"packages":{"mockly\n":{"version":"1.0.0","skills":["a"]}}}""")]
    [InlineData("""{"version":1,"packages":{"mockly":null}}""")]
    [InlineData("""{"version":1,"packages":{"mockly":{"skills":["a"]}}}""")]
    [InlineData("""{"version":1,"packages":{"mockly":{"version":" ","skills":["a"]}}}""")]
    [InlineData("""{"version":1,"packages":{"mockly":{"version":1,"skills":["a"]}}}""")]
    [InlineData("""{"version":1,"packages":{"mockly":{"version":"1.0.0"}}}""")]
    [InlineData("""{"version":1,"packages":{"mockly":{"version":"1.0.0","skills":"a"}}}""")]
    [InlineData("""{"version":1,"packages":{"mockly":{"version":"1.0.0","skills":[null]}}}""")]
    [InlineData("""{"version":1,"packages":{"mockly":{"version":"1.0.0","skills":["../outside"]}}}""")]
    [InlineData("""{"version":1,"packages":{"alpha":{"version":"1.0.0","skills":["shared"]},"beta":{"version":"1.0.0","skills":["SHARED"]}}}""")]
    [InlineData("""{"version":1,"packages":{"alpha":{"version":"1.0.0","skills":["shared","shared"]}}}""")]
    public void A_manifest_with_an_unusable_shape_fails_before_anything_uses_it(string contents)
    {
        using var temp = new TempDirectory();
        var destination = temp.CreateDirectory("dest");
        var path = temp.CreateFile("dest/.dotnet-package-skills.json", contents);

        var error = Assert.Throws<PackageSkillsException>(() => InstallManifest.Load(destination));

        Assert.Contains("Could not read the install manifest", error.Message);
        Assert.Contains("preserved", error.Message);
        Assert.Equal(contents, File.ReadAllText(path));
    }

    [Fact]
    public void The_first_save_creates_an_ordinary_manifest_file()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("dest");
        var path = Path.Combine(destination, InstallManifest.FileName);
        var manifest = new InstallManifest();
        manifest.SetSkills([new TrackedSkill("New", "2.0.0", "new")]);

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
