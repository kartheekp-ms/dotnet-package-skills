namespace DotnetPackageSkills.Tests;

/// <summary>A scratch directory that cleans itself up, for tests that touch the file system.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dps-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Creates a directory under the temp root and returns its full path.</summary>
    public string CreateDirectory(params string[] parts)
    {
        var full = Combine(parts);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Creates a file (and its parent directories) under the temp root.</summary>
    public string CreateFile(string relativePath, string content = "")
    {
        var full = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Builds an extracted-package layout with a bundled skill, mirroring the NuGet cache.</summary>
    public string CreatePackageWithSkill(string packageId, string version, params string[] skillNames)
    {
        var packageDirectory = CreateDirectory("packages", packageId.ToLowerInvariant(), version);

        foreach (var skillName in skillNames)
        {
            CreateFile($"packages/{packageId.ToLowerInvariant()}/{version}/skills/{skillName}/SKILL.md",
                $"---\nname: {skillName}\n---\n");
        }

        return packageDirectory;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked file in a temp directory is not worth failing a test over.
        }
    }
}
