namespace DotnetPackageSkills.NuGet;

/// <summary>
/// Maps a package id and version to its folder inside the global packages cache.
/// </summary>
/// <remarks>
/// Restore extracts each package to <c>&lt;global-packages&gt;/&lt;id&gt;/&lt;version&gt;/</c>
/// with both segments lowercased and the version normalized. This mirrors NuGet's own
/// normalization rules; where they are ambiguous, a directory scan settles it, so a
/// mismatch degrades into a slower lookup rather than a missed package.
/// </remarks>
public static class PackagePathResolver
{
    /// <summary>Returns the extracted package folder, or null when it is not on disk.</summary>
    public static string? Resolve(string globalPackagesFolder, string packageId, string version)
    {
        var packageDirectory = Path.Combine(globalPackagesFolder, packageId.ToLowerInvariant());

        if (!Directory.Exists(packageDirectory))
        {
            return null;
        }

        var normalized = NormalizeVersion(version);

        var candidate = Path.Combine(packageDirectory, normalized);
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        // Fall back to a case-insensitive scan. NuGet's normalization has corner cases
        // (SemVer 2 build metadata, unusual padding) that are not worth reimplementing
        // exactly, and the directory itself is the authoritative answer.
        foreach (var directory in Directory.EnumerateDirectories(packageDirectory))
        {
            var name = Path.GetFileName(directory);
            if (name.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(version, StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }
        }

        return null;
    }

    /// <summary>
    /// Normalizes a version the way NuGet does for folder names: lowercased, build
    /// metadata dropped, padded to three parts, and a fourth part dropped when zero.
    /// So <c>1.2</c> becomes <c>1.2.0</c> and <c>1.2.3.0</c> becomes <c>1.2.3</c>.
    /// </summary>
    public static string NormalizeVersion(string version)
    {
        var value = version.Trim();

        // Build metadata is not part of package identity and never appears in the path.
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        var dash = value.IndexOf('-');
        var core = dash >= 0 ? value[..dash] : value;
        var prerelease = dash >= 0 ? value[(dash + 1)..] : string.Empty;

        var parts = core.Split('.');
        var numbers = new List<int>(4);

        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var number) || number < 0)
            {
                // Not a version shape this tool understands; leave it to the directory scan.
                return version.Trim().ToLowerInvariant();
            }

            numbers.Add(number);
        }

        while (numbers.Count < 3)
        {
            numbers.Add(0);
        }

        if (numbers.Count >= 4 && numbers[3] == 0)
        {
            numbers.RemoveRange(3, numbers.Count - 3);
        }

        var normalized = string.Join('.', numbers);

        if (prerelease.Length > 0)
        {
            normalized = $"{normalized}-{prerelease}";
        }

        return normalized.ToLowerInvariant();
    }
}
