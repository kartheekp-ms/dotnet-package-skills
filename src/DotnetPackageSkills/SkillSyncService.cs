using DotnetPackageSkills.Infrastructure;
using DotnetPackageSkills.NuGet;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills;

/// <summary>Inputs for an install, sync, or list.</summary>
public sealed record SyncRequest
{
    /// <summary>Solution or project to inspect. Ignored when <see cref="Packages"/> is set.</summary>
    public string? Target { get; init; }

    /// <summary>Exact packages to take skills from, instead of inspecting a project.</summary>
    public IReadOnlyList<PackageCoordinate> Packages { get; init; } = [];

    public required string Destination { get; init; }
    public required string WorkingDirectory { get; init; }
    public bool AllowRestore { get; init; } = true;
    public string? GlobalPackagesOverride { get; init; }
    public bool DryRun { get; init; }
}

/// <summary>What an install, sync, or list produced.</summary>
public sealed record SyncResult
{
    /// <summary>The solution or project inspected, or null when packages were named explicitly.</summary>
    public string? Target { get; init; }

    public required string GlobalPackagesFolder { get; init; }
    public required string Destination { get; init; }
    public required int PackagesScanned { get; init; }
    public required bool DryRun { get; init; }
    public required IReadOnlyList<BundledSkill> Skills { get; init; }
    public IReadOnlyList<ManifestEntry> Removed { get; init; } = [];
    public IReadOnlyList<SkippedSkill> Skipped { get; init; } = [];

    /// <summary>
    /// Packages that were resolved but are not extracted on disk. Reported rather than treated
    /// as failure, because a partially restored tree is a normal, fixable state.
    /// </summary>
    public IReadOnlyList<string> NotOnDisk { get; init; } = [];
}

/// <summary>Ties package listing, skill discovery, and installation together.</summary>
public sealed class SkillSyncService(DotnetCli dotnet, SkillInstaller installer)
{
    public SkillSyncService(IProcessRunner runner) : this(new DotnetCli(runner), new SkillInstaller())
    {
    }

    /// <summary>Discovers bundled skills without writing anything.</summary>
    public SyncResult Discover(SyncRequest request)
    {
        return request.Packages.Count > 0
            ? DiscoverFromCoordinates(request)
            : DiscoverFromTarget(request);
    }

    private SyncResult DiscoverFromTarget(SyncRequest request)
    {
        var target = TargetLocator.Resolve(request.Target, request.WorkingDirectory);

        // Ask for the global packages folder from the repository, not from wherever the user
        // happened to invoke the tool: nuget.config discovery walks up from the current
        // directory, and a repo-level config is exactly the case worth honouring.
        var globalPackages = LocateGlobalPackages(request, Path.GetDirectoryName(target));

        // Keep every distinct (id, version) long enough to detect unsupported multi-version
        // collisions explicitly rather than silently selecting one package from the solution.
        var packages = new PackageLister(dotnet).List(target, request.AllowRestore);

        var (skills, notOnDisk, skipped) = Collect(globalPackages, packages.Select(p => (p.Id, p.Version)));

        return Build(request, target, globalPackages, packages.Count, skills, notOnDisk, skipped);
    }

    private SyncResult DiscoverFromCoordinates(SyncRequest request)
    {
        var globalPackages = LocateGlobalPackages(request, request.WorkingDirectory);

        var (skills, notOnDisk, skipped) = Collect(
            globalPackages,
            request.Packages.Select(coordinate => (coordinate.Id, coordinate.Version)));

        return Build(request, target: null, globalPackages, request.Packages.Count, skills, notOnDisk, skipped);
    }

    private string LocateGlobalPackages(SyncRequest request, string? preferredDirectory) =>
        new GlobalPackagesLocator(dotnet).Locate(
            request.GlobalPackagesOverride,
            preferredDirectory ?? request.WorkingDirectory);

    private static (List<BundledSkill> Skills, List<string> NotOnDisk, List<SkippedSkill> Skipped) Collect(
        string globalPackages,
        IEnumerable<(string Id, string Version)> packages)
    {
        var skills = new List<BundledSkill>();
        var notOnDisk = new List<string>();
        var skipped = new List<SkippedSkill>();
        var destinations = new Dictionary<string, BundledSkill>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, version) in packages)
        {
            var packageDirectory = PackagePathResolver.Resolve(globalPackages, id, version);

            if (packageDirectory is null)
            {
                notOnDisk.Add($"{id} {version}");
                continue;
            }

            foreach (var skill in SkillDiscovery.Discover(packageDirectory, id, version))
            {
                if (destinations.TryAdd(skill.RelativePath, skill))
                {
                    skills.Add(skill);
                    continue;
                }

                var retained = destinations[skill.RelativePath];
                skipped.Add(ToSkipped(
                    skill,
                    $"conflicts with {retained.PackageId} {retained.PackageVersion} skill " +
                    $"'{retained.SkillName}', which was selected first"));
            }
        }

        return (skills, notOnDisk, skipped);
    }

    private static SyncResult Build(
        SyncRequest request,
        string? target,
        string globalPackages,
        int packagesScanned,
        IReadOnlyList<BundledSkill> skills,
        IReadOnlyList<string> notOnDisk,
        IReadOnlyList<SkippedSkill> skipped) =>
        new()
        {
            Target = target,
            GlobalPackagesFolder = globalPackages,
            Destination = Path.GetFullPath(request.Destination, request.WorkingDirectory),
            PackagesScanned = packagesScanned,
            DryRun = request.DryRun,
            Skills = skills,
            NotOnDisk = notOnDisk,
            Skipped = skipped,
        };

    /// <summary>Discovers bundled skills and copies them into the destination.</summary>
    public SyncResult Sync(SyncRequest request)
    {
        var discovered = Discover(request);

        // Only a target describes a complete set of packages, so only a target licenses
        // pruning. Naming packages explicitly is additive — it says nothing about the
        // skills already installed from elsewhere.
        var outcome = installer.Install(
            discovered.Destination,
            discovered.Skills,
            request.DryRun,
            prune: request.Packages.Count == 0);

        return discovered with
        {
            Skills = outcome.Installed,
            Removed = outcome.Removed,
            Skipped = [.. discovered.Skipped, .. outcome.Skipped],
        };
    }

    /// <summary>
    /// Removes skills this tool installed, optionally limited to one package or one exact version.
    /// </summary>
    public IReadOnlyList<ManifestEntry> Uninstall(
        string destination,
        string workingDirectory,
        string? packageId,
        string? packageVersion,
        bool dryRun)
    {
        var root = Path.GetFullPath(destination, workingDirectory);
        return installer.Uninstall(root, packageId, packageVersion, dryRun);
    }

    private static SkippedSkill ToSkipped(BundledSkill skill, string reason) =>
        new(
            skill.RelativePath,
            skill.PackageId,
            skill.PackageVersion,
            skill.SkillName,
            reason);
}
