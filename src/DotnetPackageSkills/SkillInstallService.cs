using DotnetPackageSkills.Infrastructure;
using DotnetPackageSkills.NuGet;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills;

/// <summary>Inputs for an install or a list.</summary>
public sealed record InstallRequest
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

/// <summary>What an install or a list produced.</summary>
public sealed record InstallResult
{
    /// <summary>The solution or project inspected, or null when packages were named explicitly.</summary>
    public string? Target { get; init; }

    public required string GlobalPackagesFolder { get; init; }
    public required string Destination { get; init; }
    public required int PackagesScanned { get; init; }
    public required bool DryRun { get; init; }
    public required IReadOnlyList<BundledSkill> Skills { get; init; }

    /// <summary>
    /// How many skills discovery turned up, which stays put even after <see cref="Skills"/> is
    /// narrowed to what was actually installed. Without it a report cannot tell "no package ships
    /// a skill" apart from "you chose none of the ones that do".
    /// </summary>
    public int SkillsDiscovered { get; init; }

    public IReadOnlyList<TrackedSkill> Removed { get; init; } = [];
    public IReadOnlyList<SkippedSkill> Skipped { get; init; } = [];

    /// <summary>
    /// Packages that were resolved but are not extracted on disk. Discovery reports them;
    /// target-based installation must stop rather than infer removals from an incomplete set.
    /// </summary>
    public IReadOnlyList<string> NotOnDisk { get; init; } = [];

    internal IReadOnlyList<PackageReferenceInfo> ResolvedPackages { get; init; } = [];
    internal IReadOnlyList<BundledSkill>? AllCandidates { get; init; }
}

/// <summary>Which discovered skills the user chose, and which installed ones they turned off.</summary>
/// <param name="Selected">Skills to install.</param>
/// <param name="Deselected">
/// Destination paths that were installed and are no longer wanted. These are removed even when
/// the request would otherwise be additive.
/// </param>
public sealed record SkillChoice(
    IReadOnlyList<BundledSkill> Selected,
    IReadOnlyList<string> Deselected)
{
    public IReadOnlyCollection<TrackedSkill>? ExpectedInstalled { get; init; }
}

/// <summary>Ties package listing, skill discovery, and installation together.</summary>
public sealed class SkillInstallService(DotnetCli dotnet, SkillInstaller installer)
{
    public SkillInstallService(IProcessRunner runner) : this(new DotnetCli(runner), new SkillInstaller())
    {
    }

    /// <summary>Discovers bundled skills without writing anything.</summary>
    public InstallResult Discover(InstallRequest request)
    {
        return request.Packages.Count > 0
            ? DiscoverFromCoordinates(request)
            : DiscoverFromTarget(request);
    }

    private InstallResult DiscoverFromTarget(InstallRequest request)
    {
        var target = TargetLocator.Resolve(request.Target, request.WorkingDirectory);

        // Ask for the global packages folder from the repository, not from wherever the user
        // happened to invoke the tool: nuget.config discovery walks up from the current
        // directory, and a repo-level config is exactly the case worth honouring.
        var globalPackages = LocateGlobalPackages(request, Path.GetDirectoryName(target));

        // Keep every distinct (id, version) long enough to detect unsupported multi-version
        // collisions explicitly rather than silently selecting one package from the solution.
        var packages = new PackageLister(dotnet).List(target, request.AllowRestore);

        var (skills, notOnDisk, skipped, candidates) = Collect(globalPackages, packages.Select(p => (p.Id, p.Version)));

        return Build(request, target, globalPackages, packages.Count, skills, notOnDisk, skipped)
            with { ResolvedPackages = packages, AllCandidates = candidates };
    }

    private InstallResult DiscoverFromCoordinates(InstallRequest request)
    {
        var globalPackages = LocateGlobalPackages(request, request.WorkingDirectory);

        var packages = request.Packages.DistinctBy(package =>
            (package.Id.ToLowerInvariant(), PackagePathResolver.NormalizeVersion(package.Version))).ToArray();
        var (skills, notOnDisk, skipped, candidates) = Collect(
            globalPackages,
            packages.Select(coordinate => (coordinate.Id, coordinate.Version)));

        return Build(request, target: null, globalPackages, packages.Length, skills, notOnDisk, skipped)
            with { AllCandidates = candidates };
    }

    private string LocateGlobalPackages(InstallRequest request, string? preferredDirectory) =>
        new GlobalPackagesLocator(dotnet).Locate(
            request.GlobalPackagesOverride,
            preferredDirectory ?? request.WorkingDirectory);

    private static (List<BundledSkill> Skills, List<string> NotOnDisk, List<SkippedSkill> Skipped,
        List<BundledSkill> Candidates) Collect(
        string globalPackages,
        IEnumerable<(string Id, string Version)> packages)
    {
        var skills = new List<BundledSkill>();
        var notOnDisk = new List<string>();
        var skipped = new List<SkippedSkill>();
        var candidates = new List<BundledSkill>();
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
                candidates.Add(skill);
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

        return (skills, notOnDisk, skipped, candidates);
    }

    private static InstallResult Build(
        InstallRequest request,
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
            SkillsDiscovered = skills.Count,
            NotOnDisk = notOnDisk,
            Skipped = skipped,
        };

    /// <summary>Discovers bundled skills and copies them into the destination.</summary>
    public InstallResult Install(InstallRequest request) => Install(request, Discover(request), choice: null);

    /// <summary>
    /// Copies a caller-chosen subset of already-discovered skills, which is what the interactive
    /// picker produces. Passing a null <paramref name="choice"/> installs everything discovered.
    /// </summary>
    public InstallResult Install(InstallRequest request, InstallResult discovered, SkillChoice? choice)
    {
        var missing = request.Packages.Count == 0
            ? discovered.NotOnDisk.Concat(discovered.ResolvedPackages
                    .Where(package => PackagePathResolver.Resolve(discovered.GlobalPackagesFolder, package.Id, package.Version) is null)
                    .Select(package => $"{package.Id} {package.Version}"))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        if (missing.Length > 0)
        {
            throw new PackageSkillsException(
                $"Cannot install skills because resolved packages are missing from '{discovered.GlobalPackagesFolder}': " +
                $"{string.Join(", ", missing)}. " +
                "Run dotnet restore for the target using this cache, then try again. No skills were changed.");
        }

        // Automatic cleanup requires a complete target and no interactive choice. A picker
        // can remove only the installed skills its user explicitly deselected.
        var outcome = installer.Install(
            discovered.Destination,
            choice?.Selected ?? discovered.AllCandidates ?? discovered.Skills,
            request.DryRun,
            prune: request.Packages.Count == 0 && choice is null,
            deselected: choice?.Deselected,
            expectedInstalled: choice?.ExpectedInstalled);

        return discovered with
        {
            DryRun = request.DryRun,
            Skills = outcome.Installed,
            Removed = outcome.Removed,
            Skipped = discovered.AllCandidates is null
                ? [.. discovered.Skipped, .. outcome.Skipped]
                : outcome.Skipped,
            AllCandidates = null,
        };
    }

    internal InstallResult PrepareInteractiveInstall(
        InstallRequest request,
        InstallResult discovered,
        IReadOnlyCollection<TrackedSkill> installed) =>
        Install(
            request with { DryRun = true },
            discovered,
            new SkillChoice(discovered.AllCandidates ?? discovered.Skills, []) { ExpectedInstalled = installed })
        with { DryRun = request.DryRun };

    /// <summary>Skill folder names the manifest in <paramref name="destination"/> already tracks.</summary>
    public static IReadOnlySet<string> InstalledSkillNames(string destination) =>
        InstalledSkills(destination, Directory.GetCurrentDirectory())
            .Select(entry => entry.Skill)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Everything the manifest tracks, in the order a list should show it.
    /// </summary>
    /// <remarks>
    /// This is what uninstall offers to choose from. It reads the manifest rather than the
    /// folder, so skills the user wrote themselves are never on the list — the same reason
    /// removal is manifest-driven in the first place.
    /// </remarks>
    public static IReadOnlyList<TrackedSkill> InstalledSkills(string destination, string workingDirectory)
    {
        var root = Path.GetFullPath(destination, workingDirectory);
        using var destinationLock = DestinationLock.Acquire(root);
        return
        [
            .. InstallManifest.Load(root)
                .EnumerateSkills()
                .OrderBy(entry => entry.Skill, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Skill, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Removes skills this tool installed, optionally limited to one package, one exact
    /// version, or the names the caller chose.
    /// </summary>
    public IReadOnlyList<TrackedSkill> Uninstall(
        string destination,
        string workingDirectory,
        string? packageId,
        string? packageVersion,
        bool dryRun,
        IReadOnlyCollection<string>? only = null,
        IReadOnlyCollection<TrackedSkill>? expectedInstalled = null)
    {
        var root = Path.GetFullPath(destination, workingDirectory);
        return installer.Uninstall(root, packageId, packageVersion, dryRun, only, expectedInstalled);
    }

    private static SkippedSkill ToSkipped(BundledSkill skill, string reason) =>
        new(
            skill.RelativePath,
            skill.PackageId,
            skill.PackageVersion,
            skill.SkillName,
            reason);
}
