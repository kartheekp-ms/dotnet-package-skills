using System.Text;
using System.Text.Json;
using DotnetPackageSkills.NuGet;

namespace DotnetPackageSkills.Skills;

/// <summary>One installed skill with its owning package metadata.</summary>
public sealed record TrackedSkill(string Package, string Version, string Skill);

/// <summary>The skills this tool installed from one package, and the one version they came from.</summary>
public sealed record ManifestPackage(string Version, IReadOnlyList<string> Skills);

/// <summary>
/// Record of what this tool put in the destination folder.
/// </summary>
/// <remarks>
/// The manifest is what makes removal safe. Refreshing, removal and uninstall act only on skill
/// folder names recorded under their owning package, never on whatever happens to be in the
/// destination, so hand-authored skills living alongside package-provided ones are never at risk.
///
/// The layout follows <c>dotnet-tools.json</c>: a format <c>version</c>, then a <c>packages</c> map
/// keyed by lowercase package id, each with the one version its skills came from. Repositories
/// commit this file, so the bytes are deterministic: sorted, normalized, and LF-only on every
/// operating system.
/// </remarks>
public sealed class InstallManifest
{
    public const string FileName = ".dotnet-package-skills.json";

    /// <summary>
    /// The only format this build reads and writes. A newer tool that changes the format raises
    /// this number, and this build then refuses the file rather than dropping what it can't read.
    /// </summary>
    public const int FormatVersion = 1;

    private SortedDictionary<string, ManifestPackage> _packages = new(StringComparer.Ordinal);

    /// <summary>Tracked packages by lowercase id.</summary>
    public IReadOnlyDictionary<string, ManifestPackage> Packages => _packages;

    internal bool IsEmpty => _packages.Count == 0;

    /// <summary>Loads and validates the manifest without changing it.</summary>
    /// <remarks>
    /// An unreadable manifest cannot safely mean "nothing is tracked." Doing that makes every
    /// folder this tool installed look user-owned, so install refuses to update it and uninstall
    /// refuses to remove it. Stop instead: ownership is unknown, and guessing could overwrite or
    /// delete a hand-authored skill.
    /// </remarks>
    public static InstallManifest Load(string destinationRoot)
    {
        var path = Path.Combine(destinationRoot, FileName);

        if (!File.Exists(path))
        {
            return new InstallManifest();
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw CannotRead(path, "the file could not be opened", ex);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw CannotRead(path, "it does not contain valid manifest JSON", ex);
        }

        using (document)
        {
            return Read(document.RootElement, path);
        }
    }

    private static InstallManifest Read(JsonElement root, string path)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw CannotRead(path, "it must contain a JSON object");
        }

        if (FindDuplicateProperty(root) is { } duplicate)
        {
            throw CannotRead(path, $"the property '{duplicate}' appears more than once");
        }

        if (!TryGetProperty(root, "version", out var formatVersion))
        {
            throw TryGetProperty(root, "installed", out _) && !TryGetProperty(root, "packages", out _)
                ? WrittenByPreRelease(path)
                : CannotRead(path, "it has no format 'version'");
        }

        if (formatVersion.ValueKind != JsonValueKind.Number || !formatVersion.TryGetInt32(out var number))
        {
            throw CannotRead(path, "its format 'version' must be a whole number");
        }

        if (number > FormatVersion)
        {
            throw WrittenByNewerTool(path, number);
        }

        if (number < 1)
        {
            throw CannotRead(path, $"format version {number} is not supported");
        }

        if (!TryGetProperty(root, "packages", out var packages) || packages.ValueKind != JsonValueKind.Object)
        {
            throw CannotRead(path, "'packages' must be an object");
        }

        var manifest = new InstallManifest();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in packages.EnumerateObject())
        {
            var id = package.Name;

            if (!PackageCoordinate.IsValidId(id))
            {
                throw CannotRead(path, $"'{id}' is not a valid package id");
            }

            if (package.Value.ValueKind != JsonValueKind.Object)
            {
                throw CannotRead(path, $"'packages.{id}' must be an object");
            }

            if (!TryGetProperty(package.Value, "version", out var version) ||
                version.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(version.GetString()))
            {
                throw CannotRead(path, $"'packages.{id}.version' must be text");
            }

            if (!TryGetProperty(package.Value, "skills", out var skills) || skills.ValueKind != JsonValueKind.Array)
            {
                throw CannotRead(path, $"'packages.{id}.skills' must be an array");
            }

            var names = new List<string>();
            foreach (var skill in skills.EnumerateArray())
            {
                var name = skill.ValueKind == JsonValueKind.String ? skill.GetString() : null;

                if (name is null || !SkillDiscovery.IsSafeSkillName(name))
                {
                    throw CannotRead(
                        path,
                        $"'packages.{id}.skills[{names.Count}]' is not a safe skill folder name");
                }

                if (!claimed.Add(name))
                {
                    throw CannotRead(path, $"the skill folder '{name}' is claimed more than once");
                }

                names.Add(name);
            }

            manifest._packages[id.ToLowerInvariant()] = new ManifestPackage(version.GetString()!, names);
        }

        return manifest;
    }

    /// <summary>Names are matched without regard to case, as they were in every earlier build.</summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? FindDuplicateProperty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    return property.Name;
                }

                if (FindDuplicateProperty(property.Value) is { } nested)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (FindDuplicateProperty(item) is { } nested)
                {
                    return nested;
                }
            }
        }

        return null;
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

    private static PackageSkillsException WrittenByNewerTool(string path, int formatVersion) =>
        new(
            $"Could not read the install manifest '{path}' because it uses format version {formatVersion}, " +
            $"and this version of dotnet-package-skills supports only version {FormatVersion}. " +
            "No skills were changed and the file was preserved. Update dotnet-package-skills, and then try again.");

    private static PackageSkillsException WrittenByPreRelease(string path) =>
        new(
            $"Could not read the install manifest '{path}' because it was written by a pre-release version " +
            "of dotnet-package-skills. No skills were changed and the file was preserved. " +
            "Move the skills folder aside, and then run install again.");

    public void Save(string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);

        // Rewrite the existing file rather than replacing it, which keeps its permissions.
        File.WriteAllText(
            Path.Combine(destinationRoot, FileName),
            Serialize(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private string Serialize()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", FormatVersion);
            writer.WriteStartObject("packages");

            foreach (var (id, package) in _packages)
            {
                writer.WriteStartObject(id);
                writer.WriteString("version", package.Version);
                writer.WriteStartArray("skills");

                foreach (var skill in package.Skills)
                {
                    writer.WriteStringValue(skill);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        // .NET 8 indents with the platform newline. Values are escaped, so every CRLF here is
        // the writer's own, and replacing them makes a Windows file identical to a Linux one.
        return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    internal IEnumerable<TrackedSkill> EnumerateSkills() =>
        _packages.SelectMany(package =>
            package.Value.Skills.Select(skill => new TrackedSkill(package.Key, package.Value.Version, skill)));

    /// <summary>Replaces everything tracked. Nothing is kept if the skills break a manifest rule.</summary>
    internal void SetSkills(IEnumerable<TrackedSkill> skills)
    {
        var next = new SortedDictionary<string, ManifestPackage>(StringComparer.Ordinal);

        foreach (var group in skills.GroupBy(skill => skill.Package.ToLowerInvariant(), StringComparer.Ordinal))
        {
            // The reader refuses an invalid id, so writing one would lock every later command
            // out of the destination.
            if (!PackageCoordinate.IsValidId(group.Key))
            {
                throw new PackageSkillsException(
                    $"The install manifest can't record the package '{group.Key}' because it is not a valid package id. " +
                    "No skills were changed.");
            }

            var versions = group
                .Select(skill => PackagePathResolver.NormalizeVersion(skill.Version))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (versions.Count > 1)
            {
                throw new PackageSkillsException(
                    $"The install manifest can record only one version of each package, but skills from " +
                    $"{group.Key} {string.Join(" and ", versions)} were about to be recorded. " +
                    "No skills were changed.");
            }

            next[group.Key] = new ManifestPackage(
                versions[0],
                [
                    .. group
                        .Select(skill => skill.Skill)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(skill => skill, StringComparer.Ordinal),
                ]);
        }

        _packages = next;
    }

    public static void Delete(string destinationRoot)
    {
        var path = Path.Combine(destinationRoot, FileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
