using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetPackageSkills.NuGet;

namespace DotnetPackageSkills.Skills;

/// <summary>Skills this tool installed from one package version.</summary>
public sealed record ManifestEntry
{
    [JsonPropertyName("package")]
    public required string Package { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("skills")]
    public required List<string> Skills { get; init; }
}

/// <summary>One installed skill with its owning package metadata.</summary>
public sealed record TrackedSkill(string Package, string Version, string Skill);

/// <summary>
/// Record of what this tool put in the destination folder.
/// </summary>
/// <remarks>
/// The manifest is what makes removal safe. Pruning and uninstall act only on skill folder names
/// recorded under their owning package version, never on whatever happens to be in the
/// destination, so hand-authored skills living alongside package-provided ones are never at risk.
/// </remarks>
public sealed class InstallManifest
{
    public const string FileName = ".dotnet-package-skills.json";

    private const string NoteText =
        "Written by the dotnet-package-skills tool. Records which skills were copied out of " +
        "NuGet packages so they can be refreshed or removed safely. Do not hand-edit.";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonPropertyName("note")]
    public string Note { get; set; } = NoteText;

    [JsonPropertyName("installed")]
    public List<ManifestEntry> Installed { get; set; } = [];

    /// <summary>
    /// Loads the manifest, treating any unreadable or malformed file as empty. A corrupt
    /// manifest should degrade into "nothing is tracked yet" rather than blocking the user
    /// from installing; the worst case is a stale folder left behind, which is recoverable.
    /// </summary>
    public static InstallManifest Load(string destinationRoot)
    {
        var path = System.IO.Path.Combine(destinationRoot, FileName);

        if (!File.Exists(path))
        {
            return new InstallManifest();
        }

        try
        {
            return JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), SerializerOptions)
                ?? new InstallManifest();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new InstallManifest();
        }
    }

    public void Save(string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);

        Note = NoteText;
        SetSkills(EnumerateSkills());

        File.WriteAllText(
            System.IO.Path.Combine(destinationRoot, FileName),
            JsonSerializer.Serialize(this, SerializerOptions) + Environment.NewLine);
    }

    internal IEnumerable<TrackedSkill> EnumerateSkills() =>
        Installed.SelectMany(entry =>
            entry.Skills.Select(skill => new TrackedSkill(entry.Package, entry.Version, skill)));

    internal void SetSkills(IEnumerable<TrackedSkill> skills)
    {
        Installed =
        [
            .. skills
                .GroupBy(
                    skill => (
                        Package: skill.Package.ToLowerInvariant(),
                        Version: PackagePathResolver.NormalizeVersion(skill.Version)))
                .Select(group =>
                {
                    var first = group.First();
                    return new ManifestEntry
                    {
                        Package = first.Package,
                        Version = first.Version,
                        Skills =
                        [
                            .. group
                                .Select(skill => skill.Skill)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(skill => skill, StringComparer.Ordinal),
                        ],
                    };
                })
                .OrderBy(entry => entry.Package, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Version, StringComparer.Ordinal),
        ];
    }

    public static void Delete(string destinationRoot)
    {
        var path = System.IO.Path.Combine(destinationRoot, FileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
