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

    /// <summary>Loads and validates the manifest without changing it.</summary>
    /// <remarks>
    /// An unreadable manifest cannot safely mean "nothing is tracked." Doing that makes every
    /// folder this tool installed look user-owned, so install refuses to update it and uninstall
    /// refuses to remove it. Stop instead: ownership is unknown, and guessing could overwrite or
    /// delete a hand-authored skill.
    /// </remarks>
    public static InstallManifest Load(string destinationRoot)
    {
        var path = System.IO.Path.Combine(destinationRoot, FileName);

        if (!File.Exists(path))
        {
            return new InstallManifest();
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<InstallManifest>(
                File.ReadAllText(path),
                SerializerOptions);

            return Validate(manifest, path);
        }
        catch (JsonException ex)
        {
            throw CannotRead(path, "it is not valid JSON", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw CannotRead(path, "the file could not be opened", ex);
        }
    }

    private static InstallManifest Validate(InstallManifest? manifest, string path)
    {
        if (manifest is null)
        {
            throw CannotRead(path, "it must contain a JSON object");
        }

        if (manifest.Installed is null)
        {
            throw CannotRead(path, "'installed' must be an array");
        }

        for (var entryIndex = 0; entryIndex < manifest.Installed.Count; entryIndex++)
        {
            var entry = manifest.Installed[entryIndex];

            if (entry is null)
            {
                throw CannotRead(path, $"'installed[{entryIndex}]' must be an object");
            }

            if (string.IsNullOrWhiteSpace(entry.Package))
            {
                throw CannotRead(path, $"'installed[{entryIndex}].package' must be text");
            }

            if (string.IsNullOrWhiteSpace(entry.Version))
            {
                throw CannotRead(path, $"'installed[{entryIndex}].version' must be text");
            }

            if (entry.Skills is null)
            {
                throw CannotRead(path, $"'installed[{entryIndex}].skills' must be an array");
            }

            for (var skillIndex = 0; skillIndex < entry.Skills.Count; skillIndex++)
            {
                if (!SkillDiscovery.IsSafeSkillName(entry.Skills[skillIndex]))
                {
                    throw CannotRead(
                        path,
                        $"'installed[{entryIndex}].skills[{skillIndex}]' is not a safe skill folder name");
                }
            }
        }

        return manifest;
    }

    private static PackageSkillsException CannotRead(
        string path,
        string reason,
        Exception? inner = null) =>
        new(
            $"Could not read the install manifest '{path}' because {reason}. " +
            "No skills were changed and the file was preserved. Resolve any merge conflict or " +
            "restore the file, then try again. If it cannot be recovered, move the destination " +
            "folder aside before reinstalling.",
            inner);

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
