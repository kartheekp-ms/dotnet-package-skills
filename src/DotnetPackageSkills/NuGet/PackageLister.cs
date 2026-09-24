using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetPackageSkills.Infrastructure;

namespace DotnetPackageSkills.NuGet;

/// <summary>A package the target resolves to, after de-duplication across projects and frameworks.</summary>
public sealed record PackageReferenceInfo(string Id, string Version);

/// <summary>
/// Lists the packages a solution or project resolves to, by way of
/// <c>dotnet list &lt;target&gt; package --format json</c>.
/// </summary>
public sealed class PackageLister(DotnetCli dotnet)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Runs <c>dotnet list package</c> as it is. Whether it restores is the SDK's call: the .NET 10
    /// SDK restores when it needs to, and earlier SDKs say the target has to be restored first.
    /// </summary>
    /// <remarks>
    /// This tool never restores. A failure is reported with what the SDK said, so the customer can
    /// restore or fix whatever else it names, and then run the command again.
    /// </remarks>
    public IReadOnlyList<PackageReferenceInfo> List(string target)
    {
        // The target goes *before* the `package` verb: `dotnet list <TARGET> package`.
        var arguments = new List<string> { "list", target, "package", "--format", "json" };
        var result = dotnet.Run(arguments, workingDirectory: Path.GetDirectoryName(target));

        if (result.ExitCode != 0)
        {
            throw new PackageSkillsException(
                $"""
                 'dotnet list "{target}" package' failed with exit code {result.ExitCode}:
                 {ReportedProblems(result.StandardOutput) ?? result.Diagnostics}

                 Resolve what it reports, for example by restoring the target, and then run this command again.
                 """);
        }

        return Parse(result.StandardOutput);
    }

    /// <summary>
    /// The problems that a JSON listing reports, one per line, or null when there are none. The
    /// .NET 10 SDK reports a failed restore this way, on standard output.
    /// </summary>
    private static string? ReportedProblems(string output)
    {
        var start = output.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(output[start..]);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("problems", out var problems) ||
                problems.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var lines = problems.EnumerateArray()
                .Where(problem => problem.ValueKind == JsonValueKind.Object)
                .Select(problem => (Level: Text(problem, "level"), Text: Text(problem, "text")))
                .Where(problem => !string.IsNullOrWhiteSpace(problem.Text))
                .Select(problem => string.IsNullOrWhiteSpace(problem.Level)
                    ? problem.Text!
                    : $"{problem.Level}: {problem.Text}")
                .ToList();

            return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
        }
        catch (JsonException)
        {
            return null;
        }

        static string? Text(JsonElement problem, string name) =>
            problem.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    internal static IReadOnlyList<PackageReferenceInfo> Parse(string json)
    {
        var report = Deserialize(json);

        // Key on (id, version) because each resolved version has its own folder in the global
        // packages cache. Keeping all versions also lets skill discovery report name collisions.
        var found = new Dictionary<(string Id, string Version), PackageReferenceInfo>();

        foreach (var framework in report.Projects?.SelectMany(p => p.Frameworks ?? []) ?? [])
        {
            foreach (var entry in framework.TopLevelPackages ?? [])
            {
                var id = entry.Id?.Trim();

                // The resolved version is what exists on disk: it is the concrete value behind a
                // floating version or a version managed through Central Package Management.
                var version = Coalesce(entry.ResolvedVersion, entry.RequestedVersion);

                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version))
                {
                    continue;
                }

                var key = (id.ToLowerInvariant(), version.ToLowerInvariant());
                found[key] = new PackageReferenceInfo(id, version);
            }
        }

        return [.. found.Values.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Version, StringComparer.Ordinal)];

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

             If the error above mentions an unrecognized '--format' option, the installed SDK predates 7.0.200 and needs upgrading.
             Raw output:
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
