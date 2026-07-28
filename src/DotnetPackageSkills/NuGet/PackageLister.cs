using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetPackageSkills.Infrastructure;

namespace DotnetPackageSkills.NuGet;

/// <summary>A package the target resolves to, after de-duplication across projects and frameworks.</summary>
public sealed record PackageReferenceInfo(string Id, string Version, bool IsTransitive);

/// <summary>
/// Lists the packages a solution or project resolves to, by way of
/// <c>dotnet list &lt;target&gt; package --format json</c>.
/// </summary>
public sealed class PackageLister(DotnetCli dotnet)
{
    /// <summary>
    /// Substrings that identify "the project has not been restored" across SDK
    /// versions and locales-in-English. Matched case-insensitively against combined output.
    /// </summary>
    private static readonly string[] NotRestoredHints =
    [
        "nu1004",
        "assets file",
        "project.assets.json",
        "run a nuget package restore",
        "run restore",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public IReadOnlyList<PackageReferenceInfo> List(string target, bool includeTransitive, bool allowRestore)
    {
        // The target goes *before* the `package` verb: `dotnet list <TARGET> package`.
        var arguments = new List<string> { "list", target, "package", "--format", "json" };
        if (includeTransitive)
        {
            arguments.Add("--include-transitive");
        }

        var result = dotnet.Run(arguments, workingDirectory: Path.GetDirectoryName(target));

        if (result.ExitCode != 0 && LooksUnrestored(result))
        {
            if (!allowRestore)
            {
                throw new PackageSkillsException(
                    $"""
                     '{target}' has not been restored, so its package list is unavailable.
                     Run: dotnet restore "{target}"
                     Or drop --no-restore to let this tool restore for you.
                     """);
            }

            Restore(target);
            result = dotnet.Run(arguments, workingDirectory: Path.GetDirectoryName(target));
        }

        if (result.ExitCode != 0)
        {
            throw new PackageSkillsException(
                $"""
                 'dotnet list "{target}" package' failed with exit code {result.ExitCode}:
                 {result.Diagnostics}
                 """);
        }

        return Parse(result.StandardOutput, includeTransitive);
    }

    private void Restore(string target)
    {
        var restore = dotnet.Run("restore", target);
        if (restore.ExitCode != 0)
        {
            throw new PackageSkillsException(
                $"""
                 'dotnet restore "{target}"' failed with exit code {restore.ExitCode}:
                 {restore.Diagnostics}

                 Restore has to succeed before bundled skills can be located, because the
                 packages are only extracted to disk during restore.
                 """);
        }
    }

    private static bool LooksUnrestored(ProcessResult result)
    {
        var combined = $"{result.StandardOutput}\n{result.StandardError}";
        return NotRestoredHints.Any(hint => combined.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<PackageReferenceInfo> Parse(string json, bool includeTransitive)
    {
        var report = Deserialize(json);

        // Key on (id, version) so a package referenced by several projects, or pinned
        // to different versions per target framework, yields one entry per distinct
        // version — each of which has its own folder in the global packages cache.
        var found = new Dictionary<(string Id, string Version), PackageReferenceInfo>();

        foreach (var framework in report.Projects?.SelectMany(p => p.Frameworks ?? []) ?? [])
        {
            Collect(framework.TopLevelPackages, isTransitive: false);

            if (includeTransitive)
            {
                Collect(framework.TransitivePackages, isTransitive: true);
            }
        }

        return [.. found.Values.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Version, StringComparer.Ordinal)];

        void Collect(List<ListPackageEntry>? entries, bool isTransitive)
        {
            foreach (var entry in entries ?? [])
            {
                var id = entry.Id?.Trim();

                // Transitive entries carry only a resolved version; top-level entries
                // carry both, and the resolved one is what exists on disk (it is the
                // concrete value behind a floating version or a CPM-managed version).
                var version = Coalesce(entry.ResolvedVersion, entry.RequestedVersion);

                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version))
                {
                    continue;
                }

                var key = (id.ToLowerInvariant(), version.ToLowerInvariant());

                // A package that is both a direct and a transitive reference is direct.
                if (found.TryGetValue(key, out var existing) && !existing.IsTransitive)
                {
                    continue;
                }

                found[key] = new PackageReferenceInfo(id, version, isTransitive);
            }
        }

        static string? Coalesce(string? first, string? second) =>
            string.IsNullOrWhiteSpace(first) ? second?.Trim() : first.Trim();
    }

    private static ListPackageReport Deserialize(string json)
    {
        // MSBuild sometimes writes warnings ahead of the payload, so fall back to the
        // first '{' rather than assuming the whole stream is JSON.
        foreach (var candidate in Candidates(json))
        {
            try
            {
                var report = JsonSerializer.Deserialize<ListPackageReport>(candidate, JsonOptions);
                if (report is not null)
                {
                    return report;
                }
            }
            catch (JsonException)
            {
                // Try the next candidate.
            }
        }

        throw new PackageSkillsException(
            $"""
             Could not parse the output of 'dotnet list package --format json'.

             If the error above mentions an unrecognized '--format' option, the installed
             SDK predates 7.0.200 and needs upgrading. Raw output:
             {json.Trim()}
             """);

        static IEnumerable<string> Candidates(string text)
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                yield break;
            }

            yield return trimmed;

            var start = trimmed.IndexOf('{');
            if (start > 0)
            {
                yield return trimmed[start..];
            }
        }
    }

    private sealed class ListPackageReport
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("projects")]
        public List<ListPackageProject>? Projects { get; set; }
    }

    private sealed class ListPackageProject
    {
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("frameworks")]
        public List<ListPackageFramework>? Frameworks { get; set; }
    }

    private sealed class ListPackageFramework
    {
        [JsonPropertyName("framework")]
        public string? Framework { get; set; }

        [JsonPropertyName("topLevelPackages")]
        public List<ListPackageEntry>? TopLevelPackages { get; set; }

        [JsonPropertyName("transitivePackages")]
        public List<ListPackageEntry>? TransitivePackages { get; set; }
    }

    private sealed class ListPackageEntry
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("requestedVersion")]
        public string? RequestedVersion { get; set; }

        [JsonPropertyName("resolvedVersion")]
        public string? ResolvedVersion { get; set; }
    }
}
