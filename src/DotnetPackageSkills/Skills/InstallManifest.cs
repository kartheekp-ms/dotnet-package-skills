using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetPackageSkills.Skills;

/// <summary>One skill this tool has installed.</summary>
public sealed record ManifestEntry
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("package")]
    public required string Package { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("skill")]
    public required string Skill { get; init; }
}

/// <summary>
/// Record of what this tool put in the destination folder.
/// </summary>
/// <remarks>
/// The manifest is what makes removal safe. Pruning and uninstall act only on paths
/// recorded here, never on whatever happens to be in the destination, so hand-authored
/// skills living alongside package-provided ones are never at risk.
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
        Installed = [.. Installed.OrderBy(entry => entry.Path, StringComparer.Ordinal)];

        File.WriteAllText(
            System.IO.Path.Combine(destinationRoot, FileName),
            JsonSerializer.Serialize(this, SerializerOptions) + Environment.NewLine);
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
