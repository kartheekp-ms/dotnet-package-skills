using DotnetPackageSkills.NuGet;

namespace DotnetPackageSkills.Skills;

/// <summary>Outcome of an install or sync.</summary>
public sealed record InstallOutcome(
    IReadOnlyList<BundledSkill> Installed,
    IReadOnlyList<TrackedSkill> Removed,
    IReadOnlyList<SkippedSkill> Skipped);

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
    /// <param name="deselected">
    /// Destination paths the user explicitly chose not to install. These are removed even when
    /// <paramref name="prune"/> is false, because pruning is an inference drawn from a complete
    /// package set whereas a deselection is a direct instruction. Only paths the user was
    /// actually shown belong here.
    /// </param>
    public InstallOutcome Install(
        string destinationRoot,
        IReadOnlyList<BundledSkill> skills,
        bool dryRun,
        bool prune = true,
        IReadOnlyCollection<string>? deselected = null)
    {
        var manifest = InstallManifest.Load(destinationRoot);
        var trackedSkills = manifest.EnumerateSkills().ToList();
        var (selected, duplicateSkips) = SelectUniqueDestinations(skills);
        var accepted = new List<BundledSkill>();
        var skipped = new List<SkippedSkill>(duplicateSkips);

        foreach (var skill in selected)
        {
            var tracked = trackedSkills.FirstOrDefault(entry =>
                entry.Skill.Equals(skill.RelativePath, StringComparison.OrdinalIgnoreCase));
            var destination = ToAbsolute(destinationRoot, skill.RelativePath);

            if (File.Exists(destination))
            {
                skipped.Add(ToSkipped(skill, "the destination path already exists as a file"));
                continue;
            }

            if (tracked is null && Directory.Exists(destination))
            {
                skipped.Add(ToSkipped(
                    skill,
                    "the destination folder already exists and is not managed by this tool"));
                continue;
            }

            if (!prune && tracked is not null && !HasSameOwner(tracked, skill))
            {
                skipped.Add(ToSkipped(
                    skill,
                    $"the destination folder is managed for {tracked.Package} {tracked.Version} " +
                    $"skill '{tracked.Skill}'"));
                continue;
            }

            accepted.Add(skill);
        }

        var current = accepted.Select(skill => skill.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removeAnyway = deselected is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(deselected, StringComparer.OrdinalIgnoreCase);

        var stale = trackedSkills
            .Where(entry => !current.Contains(entry.Skill))
            .Where(entry => prune || removeAnyway.Contains(entry.Skill))
            .OrderBy(entry => entry.Skill, StringComparer.Ordinal)
            .ToList();

        if (dryRun)
        {
            return new InstallOutcome(accepted, stale, skipped);
        }

        // Remove before copying so a stale ancestor can never delete a freshly copied skill.
        var removed = stale.Where(entry => RemoveSkillDirectory(destinationRoot, entry.Skill)).ToList();

        foreach (var skill in accepted)
        {
            CopyDirectory(skill.SourcePath, ToAbsolute(destinationRoot, skill.RelativePath));
        }

        var installed = accepted.Select(skill =>
            new TrackedSkill(skill.PackageId, skill.PackageVersion, skill.SkillName));

        var next = prune
            ? installed
            // Additive: keep what was already tracked, replacing entries we just rewrote and
            // dropping the ones the user deselected.
            : trackedSkills
                .Where(entry => !current.Contains(entry.Skill) && !removeAnyway.Contains(entry.Skill))
                .Concat(installed);

        manifest.SetSkills(next);

        if (manifest.Installed.Count == 0)
        {
            // Nothing is tracked, so there is nothing for the manifest to be the source of truth
            // about. Match uninstall rather than leaving an empty manifest, and a destination
            // folder, that the user never asked for. The folder only goes if it is empty, so
            // skills they wrote themselves keep it alive.
            InstallManifest.Delete(destinationRoot);
            TryRemoveEmptyDirectory(destinationRoot);
        }
        else
        {
            manifest.Save(destinationRoot);
        }

        return new InstallOutcome(accepted, removed, skipped);
    }

    /// <summary>
    /// Removes skills this tool installed, optionally narrowed to one package or one exact
    /// version of it.
    /// </summary>
    public IReadOnlyList<TrackedSkill> Uninstall(
        string destinationRoot,
        string? packageId,
        string? packageVersion,
        bool dryRun)
    {
        var manifest = InstallManifest.Load(destinationRoot);

        var trackedSkills = manifest.EnumerateSkills().ToList();
        var targeted = trackedSkills
            .Where(entry => Matches(entry, packageId, packageVersion))
            .OrderBy(entry => entry.Skill, StringComparer.Ordinal)
            .ToList();

        if (targeted.Count == 0 || dryRun)
        {
            return targeted;
        }

        foreach (var entry in targeted)
        {
            RemoveSkillDirectory(destinationRoot, entry.Skill);
        }

        manifest.SetSkills(trackedSkills.Except(targeted));

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

    private static bool Matches(TrackedSkill entry, string? packageId, string? packageVersion)
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
    /// Deletes an installed skill folder, then any empty parent directories below the destination.
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

    private static (List<BundledSkill> Selected, List<SkippedSkill> Skipped) SelectUniqueDestinations(
        IReadOnlyList<BundledSkill> skills)
    {
        var selected = new List<BundledSkill>();
        var skipped = new List<SkippedSkill>();
        var destinations = new Dictionary<string, BundledSkill>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in skills)
        {
            if (destinations.TryAdd(skill.RelativePath, skill))
            {
                selected.Add(skill);
                continue;
            }

            var retained = destinations[skill.RelativePath];
            skipped.Add(ToSkipped(
                skill,
                $"conflicts with {retained.PackageId} {retained.PackageVersion} skill " +
                $"'{retained.SkillName}', which was selected first"));
        }

        return (selected, skipped);
    }

    private static bool HasSameOwner(TrackedSkill entry, BundledSkill skill) =>
        entry.Package.Equals(skill.PackageId, StringComparison.OrdinalIgnoreCase) &&
        entry.Skill.Equals(skill.SkillName, StringComparison.OrdinalIgnoreCase);

    private static SkippedSkill ToSkipped(BundledSkill skill, string reason) =>
        new(
            skill.RelativePath,
            skill.PackageId,
            skill.PackageVersion,
            skill.SkillName,
            reason);
}
