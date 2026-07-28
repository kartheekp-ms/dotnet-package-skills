using DotnetPackageSkills.NuGet;

namespace DotnetPackageSkills.Skills;

/// <summary>Outcome of an install or sync.</summary>
public sealed record InstallOutcome(IReadOnlyList<BundledSkill> Installed, IReadOnlyList<ManifestEntry> Removed);

/// <summary>Copies discovered skills into the destination and keeps the manifest in step.</summary>
public sealed class SkillInstaller
{
    /// <summary>
    /// Copies every skill into <paramref name="destinationRoot"/>.
    /// </summary>
    /// <param name="prune">
    /// When true, skills this tool installed previously that are not in
    /// <paramref name="skills"/> are removed — which is what makes a package upgrade replace
    /// the old version instead of accumulating beside it. Pass false when the caller named a
    /// few packages explicitly rather than describing a whole project, because then
    /// "not in this list" means "not asked about", not "no longer referenced".
    /// </param>
    public InstallOutcome Install(
        string destinationRoot,
        IReadOnlyList<BundledSkill> skills,
        bool dryRun,
        bool prune = true)
    {
        var manifest = InstallManifest.Load(destinationRoot);
        var current = skills.Select(skill => skill.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stale = prune
            ? manifest.Installed
                .Where(entry => !current.Contains(entry.Path))
                .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                .ToList()
            : [];

        if (dryRun)
        {
            return new InstallOutcome(skills, stale);
        }

        foreach (var skill in skills)
        {
            CopyDirectory(skill.SourcePath, ToAbsolute(destinationRoot, skill.RelativePath));
        }

        var removed = stale.Where(entry => RemoveSkillDirectory(destinationRoot, entry.Path)).ToList();

        var installed = skills.Select(skill => new ManifestEntry
        {
            Path = skill.RelativePath,
            Package = skill.PackageId,
            Version = skill.PackageVersion,
            Skill = skill.SkillName,
        });

        manifest.Installed = prune
            ? [.. installed]
            // Additive: keep what was already tracked, replacing entries we just rewrote.
            : [.. manifest.Installed.Where(entry => !current.Contains(entry.Path)), .. installed];

        manifest.Save(destinationRoot);

        return new InstallOutcome(skills, removed);
    }

    /// <summary>
    /// Removes skills this tool installed, optionally narrowed to one package or one exact
    /// version of it.
    /// </summary>
    public IReadOnlyList<ManifestEntry> Uninstall(
        string destinationRoot,
        string? packageId,
        string? packageVersion,
        bool dryRun)
    {
        var manifest = InstallManifest.Load(destinationRoot);

        var targeted = manifest.Installed
            .Where(entry => Matches(entry, packageId, packageVersion))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToList();

        if (targeted.Count == 0 || dryRun)
        {
            return targeted;
        }

        foreach (var entry in targeted)
        {
            RemoveSkillDirectory(destinationRoot, entry.Path);
        }

        manifest.Installed = [.. manifest.Installed.Except(targeted)];

        if (manifest.Installed.Count == 0)
        {
            InstallManifest.Delete(destinationRoot);
            TryRemoveEmptyDirectory(destinationRoot);
        }
        else
        {
            manifest.Save(destinationRoot);
        }

        // Report everything targeted, including entries whose folder a user had already
        // deleted by hand: they are gone either way, and the manifest no longer claims them.
        return targeted;
    }

    private static bool Matches(ManifestEntry entry, string? packageId, string? packageVersion)
    {
        if (packageId is not null && !entry.Package.Equals(packageId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Compare normalized, so 1.2 and 1.2.0 identify the same installed folder.
        return packageVersion is null ||
               PackagePathResolver.NormalizeVersion(entry.Version)
                   .Equals(PackagePathResolver.NormalizeVersion(packageVersion), StringComparison.OrdinalIgnoreCase);
    }

    private static string ToAbsolute(string destinationRoot, string relativePath) =>
        Path.Combine(destinationRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Deletes an installed skill folder, then any package/version directories the removal
    /// left empty, so an uninstall does not leave a husk of empty folders behind.
    /// </summary>
    private static bool RemoveSkillDirectory(string destinationRoot, string relativePath)
    {
        var absolute = ToAbsolute(destinationRoot, relativePath);

        if (!Directory.Exists(absolute))
        {
            return false;
        }

        Directory.Delete(absolute, recursive: true);

        var parent = Path.GetDirectoryName(absolute);
        var root = Path.GetFullPath(destinationRoot);

        while (!string.IsNullOrEmpty(parent) &&
               !Path.GetFullPath(parent).Equals(root, StringComparison.Ordinal) &&
               TryRemoveEmptyDirectory(parent))
        {
            parent = Path.GetDirectoryName(parent);
        }

        return true;
    }

    private static bool TryRemoveEmptyDirectory(string directory)
    {
        if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
        {
            return false;
        }

        try
        {
            Directory.Delete(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces the destination with a fresh copy of the source.
    /// </summary>
    /// <remarks>
    /// This copies rather than moves, and that is deliberate: the global packages folder is
    /// NuGet's content-addressable cache. It is validated during restore and shared by every
    /// project on the machine, so moving files out of it can make restore treat the cached
    /// package as corrupt and strips the skill from every other repository using it.
    /// </remarks>
    private static void CopyDirectory(string source, string destination)
    {
        if (Directory.Exists(destination))
        {
            // Delete first so files removed in a newer package version do not survive.
            Directory.Delete(destination, recursive: true);
        }

        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);

            // Files in the global packages folder are marked read-only by restore. Copying
            // carries that attribute over, which would make the next sync fail to overwrite.
            ClearReadOnly(target);
        }
    }

    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
