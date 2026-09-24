using DotnetPackageSkills.Infrastructure;
using DotnetPackageSkills.NuGet;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills;

/// <summary>Inputs for an install or a list.</summary>
public sealed record InstallRequest
{
    /// <summary>Where skills go when no destination is given.</summary>
    public const string DefaultDestination = ".agents/skills";

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
    /// Installed skills whose package the target no longer references. Install keeps them, and
    /// the report points at <c>uninstall --stale</c>, the one command that removes them.
    /// </summary>
    public IReadOnlyList<TrackedSkill> Unreferenced { get; init; } = [];

    /// <summary>
    /// The command the report suggests for removing <see cref="Unreferenced"/> skills, spelled
    /// with the target and destination this run used.
    /// </summary>
    public string StaleCommand { get; init; } = "dotnet package-skills uninstall --stale";

    /// <summary>
    /// Set when an interactive install found skills but every one is installed already or
    /// skipped, so there was nothing to choose from and no checklist was shown.
    /// </summary>
    public bool NothingNewToInstall { get; init; }

    internal IReadOnlyList<PackageReferenceInfo> ResolvedPackages { get; init; } = [];
    internal IReadOnlyList<BundledSkill>? AllCandidates { get; init; }
}

/// <summary>The skills the user checked. Installing a choice copies these and changes nothing else.</summary>
public sealed record SkillChoice(IReadOnlyList<BundledSkill> Selected)
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

        var (skills, skipped, candidates) = Collect(globalPackages, packages.Select(p => (p.Id, p.Version)));

        return Build(request, target, globalPackages, packages.Count, skills, skipped)
            with { ResolvedPackages = packages, AllCandidates = candidates };
    }

    private InstallResult DiscoverFromCoordinates(InstallRequest request)
    {
        var globalPackages = LocateGlobalPackages(request, request.WorkingDirectory);

        var packages = request.Packages.DistinctBy(package =>
            (package.Id.ToLowerInvariant(), PackagePathResolver.NormalizeVersion(package.Version))).ToArray();
        var (skills, skipped, candidates) = Collect(
            globalPackages,
            packages.Select(coordinate => (coordinate.Id, coordinate.Version)));

        return Build(request, target: null, globalPackages, packages.Length, skills, skipped)
            with
            {
                ResolvedPackages = [.. packages.Select(coordinate => new PackageReferenceInfo(coordinate.Id, coordinate.Version))],
                AllCandidates = candidates,
            };
    }

    private string LocateGlobalPackages(InstallRequest request, string? preferredDirectory) =>
        new GlobalPackagesLocator(dotnet).Locate(
            request.GlobalPackagesOverride,
            preferredDirectory ?? request.WorkingDirectory);

    /// <remarks>
    /// A package that is not in the cache contributes nothing, exactly like one that ships no
    /// skills: getting packages into the cache is restore's job, not this tool's. Only a target
    /// install checks for them, because it would otherwise report their skills as stale.
    /// </remarks>
    private static (List<BundledSkill> Skills, List<SkippedSkill> Skipped, List<BundledSkill> Candidates) Collect(
        string globalPackages,
        IEnumerable<(string Id, string Version)> packages)
    {
        var skills = new List<BundledSkill>();
        var skipped = new List<SkippedSkill>();
        var candidates = new List<BundledSkill>();
        var destinations = new Dictionary<string, BundledSkill>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, version) in packages)
        {
            var packageDirectory = PackagePathResolver.Resolve(globalPackages, id, version);

            if (packageDirectory is null)
            {
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

        return (skills, skipped, candidates);
    }

    private static InstallResult Build(
        InstallRequest request,
        string? target,
        string globalPackages,
        int packagesScanned,
        IReadOnlyList<BundledSkill> skills,
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
        RequireOneVersionPerPackage(request, discovered);

        if (request.Packages.Count == 0)
        {
            RequireEveryPackageInCache(discovered);
        }

        // A choice only adds: it never refreshes or removes what it was not asked about. Without
        // one, the run covers the packages it found in the cache, so a version that is not there
        // never causes a removal.
        var offered = choice is null
            ? discovered.ResolvedPackages
                .Where(package => PackagePathResolver.Resolve(discovered.GlobalPackagesFolder, package.Id, package.Version) is not null)
                .GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Version, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var outcome = installer.Install(
            discovered.Destination,
            choice?.Selected ?? discovered.AllCandidates ?? discovered.Skills,
            request.DryRun,
            offered,
            choice?.ExpectedInstalled,
            arguments => UninstallCommand(request, arguments));

        return discovered with
        {
            DryRun = request.DryRun,
            Skills = outcome.Installed,
            Removed = outcome.Removed,
            Skipped = discovered.AllCandidates is null
                ? [.. discovered.Skipped, .. outcome.Skipped]
                : outcome.Skipped,
            // A target lists every package it references, so anything it did not offer has left
            // the project. Named packages say nothing about the rest, so they report nothing.
            Unreferenced = request.Packages.Count == 0 && choice is null ? outcome.Untouched : [],
            StaleCommand = UninstallCommand(request, "--stale", withTarget: true),
            AllCandidates = null,
        };
    }

    /// <summary>
    /// Stops an install when a package has more than one version, whether or not it ships skills.
    /// </summary>
    /// <remarks>
    /// The manifest records one version per package, and skills describe the version they came
    /// from. With two versions there is no right answer for which guidance the repository gets,
    /// so rather than guess, ask for the versions to be aligned. Central Package Management keeps
    /// them aligned. <see cref="Discover"/> does not check this, so <c>list</c> still shows both.
    /// </remarks>
    private static void RequireOneVersionPerPackage(InstallRequest request, InstallResult discovered)
    {
        var conflicts = discovered.ResolvedPackages
            .GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => (
                group.First().Id,
                Versions: group
                    .DistinctBy(package => PackagePathResolver.NormalizeVersion(package.Version))
                    .Select(package => package.Version)
                    .ToList()))
            .Where(entry => entry.Versions.Count > 1)
            .Select(entry => $"{entry.Id} ({string.Join(", ", entry.Versions)})")
            .ToList();

        if (conflicts.Count == 0)
        {
            return;
        }

        throw new PackageSkillsException(request.Packages.Count == 0
            ? "Cannot install skills because these packages resolve to more than one version: " +
              $"{string.Join("; ", conflicts)}. Skills can come from only one version of each package. " +
              "Align the versions, for example with Central Package Management, and then try again. " +
              "No skills were changed."
            : "Cannot install skills because --package names more than one version of these packages: " +
              $"{string.Join("; ", conflicts)}. Skills can come from only one version of each package, " +
              "so name one version per package, and then try again. No skills were changed.");
    }

    /// <summary>
    /// Stops a target install when a package the target references is not in the cache.
    /// </summary>
    /// <remarks>
    /// Restoring is not this tool's job, but carrying on would read an unextracted package as
    /// one that ships nothing and report its installed skills as no longer referenced. Asking
    /// for a restore is the honest answer. Named packages never reach this check: naming one
    /// that is not in the cache simply finds no skills.
    /// </remarks>
    private static void RequireEveryPackageInCache(InstallResult discovered)
    {
        var missing = discovered.ResolvedPackages
            .Where(package => PackagePathResolver.Resolve(discovered.GlobalPackagesFolder, package.Id, package.Version) is null)
            .Select(package => $"{package.Id} {package.Version}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missing.Length > 0)
        {
            throw new PackageSkillsException(
                $"Cannot install skills because resolved packages are missing from '{discovered.GlobalPackagesFolder}': " +
                $"{string.Join(", ", missing)}. " +
                "Run dotnet restore for the target using this cache, then try again. No skills were changed.");
        }
    }

    /// <summary>
    /// Runs every check that stops an interactive install before a checklist opens, and returns
    /// what it can offer: the skills that would install cleanly and are not installed yet.
    /// </summary>
    /// <remarks>
    /// An interactive install only adds, so it cannot settle a mismatch between the installed
    /// skills and the packages. With a target, any stale skill stops it until
    /// <c>uninstall --stale</c> removes them. With named packages, an installed skill from
    /// another version of one of them stops it, because adding beside it would give that
    /// package two versions.
    /// </remarks>
    internal InstallResult PrepareInteractiveInstall(
        InstallRequest request,
        InstallResult discovered,
        IReadOnlyCollection<TrackedSkill> installed)
    {
        RequireOneVersionPerPackage(request, discovered);

        if (request.Packages.Count == 0)
        {
            RequireEveryPackageInCache(discovered);
            RequireNoStaleSkills(request, discovered, installed);
        }
        else
        {
            RequireNoOtherInstalledVersion(request, discovered, installed);
        }

        var preview = Install(
            request with { DryRun = true },
            discovered,
            new SkillChoice(discovered.AllCandidates ?? discovered.Skills) { ExpectedInstalled = installed });
        var tracked = installed.Select(entry => entry.Skill).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return preview with
        {
            DryRun = request.DryRun,
            Skills = [.. preview.Skills.Where(skill => !tracked.Contains(skill.RelativePath))],
        };
    }

    private static void RequireNoStaleSkills(
        InstallRequest request,
        InstallResult discovered,
        IReadOnlyCollection<TrackedSkill> installed)
    {
        var stale = installed
            .Where(entry => SkillInstaller.IsStale(entry, discovered.ResolvedPackages))
            .OrderBy(entry => entry.Skill, StringComparer.Ordinal)
            .ToList();

        if (stale.Count == 0)
        {
            return;
        }

        throw new PackageSkillsException(
            $"Cannot choose skills interactively because {stale.Count} installed " +
            $"{(stale.Count == 1 ? "skill doesn't" : "skills don't")} match the target: " +
            $"{string.Join(", ", stale.Select(entry => $"{entry.Skill} ({entry.Package} {entry.Version})"))}. " +
            $"Run '{UninstallCommand(request, "--stale", withTarget: true)}' first, and then try again. " +
            "No skills were changed.");
    }

    private static void RequireNoOtherInstalledVersion(
        InstallRequest request,
        InstallResult discovered,
        IReadOnlyCollection<TrackedSkill> installed)
    {
        var conflicts = discovered.ResolvedPackages
            .Select(package => (
                package.Id,
                Installed: installed.FirstOrDefault(entry =>
                    entry.Package.Equals(package.Id, StringComparison.OrdinalIgnoreCase) &&
                    !SkillInstaller.SameVersion(entry.Version, package.Version))))
            .Where(conflict => conflict.Installed is not null)
            .ToList();

        if (conflicts.Count == 0)
        {
            return;
        }

        var command = conflicts.Count == 1
            ? $"'{UninstallCommand(request, $"--package {conflicts[0].Id}")}'"
            : $"'{UninstallCommand(request, "--package <ID>")}' for each of them";
        throw new PackageSkillsException(
            $"{string.Join(" and ", conflicts.Select(conflict => $"{conflict.Id} {conflict.Installed!.Version}"))} " +
            $"{(conflicts.Count == 1 ? "is" : "are")} already installed, and an interactive install only adds " +
            $"skills, so it can't change a package's version. Run {command} first, and then try again. " +
            "No skills were changed.");
    }

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
    /// version, the names the caller chose, or the skills that are stale against a target.
    /// </summary>
    public IReadOnlyList<TrackedSkill> Uninstall(
        string destination,
        string workingDirectory,
        string? packageId,
        string? packageVersion,
        bool dryRun,
        IReadOnlyCollection<string>? only = null,
        IReadOnlyCollection<TrackedSkill>? expectedInstalled = null,
        IReadOnlyCollection<PackageReferenceInfo>? staleAgainst = null)
    {
        var root = Path.GetFullPath(destination, workingDirectory);
        return installer.Uninstall(root, packageId, packageVersion, dryRun, only, expectedInstalled, staleAgainst);
    }

    /// <summary>
    /// Finds the target and lists its direct package references, restoring it first when that
    /// is allowed and needed. This is all <c>uninstall --stale</c> reads: deciding which skills
    /// are stale needs the references, not the packages, so the NuGet cache is never consulted.
    /// </summary>
    public TargetReferences ReadReferences(string? target, string workingDirectory, bool allowRestore)
    {
        var resolved = TargetLocator.Resolve(target, workingDirectory);
        return new TargetReferences(resolved, new PackageLister(dotnet).List(resolved, allowRestore));
    }

    private static SkippedSkill ToSkipped(BundledSkill skill, string reason) =>
        new(
            skill.RelativePath,
            skill.PackageId,
            skill.PackageVersion,
            skill.SkillName,
            reason);

    /// <summary>
    /// Spells an uninstall command that a report or an error suggests, with the target and the
    /// destination this run used.
    /// </summary>
    /// <param name="withTarget">Repeat <c>--target</c>, which only <c>uninstall --stale</c> accepts.</param>
    internal static string UninstallCommand(InstallRequest request, string arguments, bool withTarget = false)
    {
        // A suggestion is only useful if running it as printed acts on the same skills folder,
        // compared against the same project.
        var command = $"dotnet package-skills uninstall {arguments}";

        if (withTarget && request.Target is not null)
        {
            command += $" --target {CommandArgument(request.Target)}";
        }

        if (!IsDefaultDestination(request))
        {
            command += $" --destination {CommandArgument(request.Destination)}";
        }

        return command;
    }

    private static bool IsDefaultDestination(InstallRequest request) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Destination, request.WorkingDirectory)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstallRequest.DefaultDestination, request.WorkingDirectory)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Quotes a value that a shell would otherwise split or reinterpret. Backslashes count:
    /// bash treats them as escapes outside quotes, and every shell reads them literally inside.
    /// </summary>
    private static string CommandArgument(string value) =>
        value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || "._-/:+@".Contains(character))
            ? value
            : $"\"{value}\"";
}

/// <summary>A solution or project and the package versions it references directly.</summary>
public sealed record TargetReferences(string Target, IReadOnlyList<PackageReferenceInfo> Packages);
