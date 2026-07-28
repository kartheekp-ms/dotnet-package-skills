using System.Text.RegularExpressions;

namespace DotnetPackageSkills.NuGet;

/// <summary>An exact package identity, written as <c>Id@Version</c> on the command line.</summary>
public sealed partial record PackageCoordinate(string Id, string Version)
{
    public const char Separator = '@';

    /// <summary>
    /// Parses <c>Id@Version</c>, rejecting anything that is not a single concrete version.
    /// </summary>
    /// <remarks>
    /// Version ranges and floating versions are refused rather than resolved. Resolving one
    /// means picking a version, and the only correct answer to "which version" comes from a
    /// project's restore — which is what <c>--target</c> is for. Guessing here would copy
    /// skills that describe a version the user does not actually reference.
    /// </remarks>
    public static PackageCoordinate Parse(string value)
    {
        var input = value?.Trim() ?? string.Empty;

        if (input.Length == 0)
        {
            throw new PackageSkillsException("--package needs a value in the form Id@Version, for example Mockly@1.10.0.");
        }

        var separator = input.IndexOf(Separator);

        if (separator < 0)
        {
            throw new PackageSkillsException(
                $"'{input}' is missing a version. Write --package as Id@Version, for example {input}@1.10.0. " +
                "To take versions from a project instead, use --target.");
        }

        if (input.IndexOf(Separator, separator + 1) >= 0)
        {
            throw new PackageSkillsException($"'{input}' has more than one '{Separator}'. Expected Id@Version.");
        }

        var id = input[..separator].Trim();
        var version = input[(separator + 1)..].Trim();

        if (id.Length == 0)
        {
            throw new PackageSkillsException($"'{input}' is missing a package id before the '{Separator}'.");
        }

        if (!PackageIdPattern().IsMatch(id))
        {
            throw new PackageSkillsException(
                $"'{id}' is not a valid package id. Ids contain only letters, digits, '.', '_' and '-'.");
        }

        if (version.Length == 0)
        {
            throw new PackageSkillsException($"'{input}' is missing a version after the '{Separator}'.");
        }

        if (IsFloatingOrRange(version))
        {
            throw new PackageSkillsException(
                $"""
                 '{version}' is a floating version or a version range, and this tool needs an exact version.
                 Write it out, for example --package {id}@1.10.0.
                 To let restore choose the version, point at a project or solution with --target instead.
                 """);
        }

        if (!ExactVersionPattern().IsMatch(version))
        {
            throw new PackageSkillsException(
                $"'{version}' is not a version this tool recognises. Expected something like 1.10.0 or 2.0.0-beta.1.");
        }

        return new PackageCoordinate(id, version);
    }

    /// <summary>Wildcards and NuGet interval notation: <c>4.*</c>, <c>[1.0,2.0)</c>, <c>(,3.0]</c>.</summary>
    private static readonly char[] RangeCharacters = ['*', '[', ']', '(', ')', ','];

    private static bool IsFloatingOrRange(string version) => version.IndexOfAny(RangeCharacters) >= 0;

    public override string ToString() => $"{Id}{Separator}{Version}";

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?$")]
    private static partial Regex PackageIdPattern();

    [GeneratedRegex(@"^\d+(\.\d+){0,3}(-[0-9A-Za-z][0-9A-Za-z.-]*)?(\+[0-9A-Za-z][0-9A-Za-z.-]*)?$")]
    private static partial Regex ExactVersionPattern();
}
