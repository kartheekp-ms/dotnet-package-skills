using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Cli;

/// <summary>Renders results for humans, or as JSON for scripts and agents.</summary>
public sealed class OutputWriter(TextWriter output)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void WriteJson(object value) => output.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    /// <param name="copied">
    /// False for <c>list</c>, which discovers without writing, so the report says "Found"
    /// rather than claiming files were placed.
    /// </param>
    public void WriteInstallReport(InstallResult result, bool copied)
    {
        WriteContext(result);

        var verb = copied
            ? result.DryRun ? "Would copy" : "Copied"
            // list discovers without writing, and it always runs as a dry run, so asking
            // DryRun first would make this branch unreachable and claim a copy was pending.
            : "Found";

        if (result.Skills.Count > 0)
        {
            output.WriteLine($"{verb} {Count(result.Skills.Count, "skill")}:");

            foreach (var skill in result.Skills)
            {
                output.WriteLine($"  {Describe(skill.RelativePath, skill.PackageId, skill.PackageVersion)}");
            }
        }
        else if (result.SkillsDiscovered > 0)
        {
            // Packages did ship skills; none of them ended up installed, because they were
            // deselected or skipped. Saying nobody ships a skill here would be a lie, and the
            // sections below already explain what happened to each one.
            output.WriteLine($"{verb} no skills.");
        }
        else if (result.NotOnDisk.Count > 0)
        {
            // We could not look inside every package, so we are in no position to say what
            // they do or do not ship. The unextracted list below says what to do about it.
            output.WriteLine("No bundled skills found.");
        }
        else
        {
            output.WriteLine("No bundled skills found. None of the scanned packages ship a skills/ folder.");
        }

        if (result.Removed.Count > 0)
        {
            output.WriteLine();
            output.WriteLine(
                $"{(result.DryRun ? "Would remove" : "Removed")} {Count(result.Removed.Count, "skill")}:");

            foreach (var entry in result.Removed)
            {
                output.WriteLine($"  {Describe(entry.Skill, entry.Package, entry.Version)}");
            }
        }

        WriteSkipped(result);
        WriteNotOnDisk(result);

        if (result.Skills.Count > 0 && copied && !result.DryRun)
        {
            output.WriteLine();

            // One line, however long. Any break we choose is a guess at the reader's width,
            // and the terminal already knows theirs.
            output.WriteLine(
                "These skills are instructions written by the package authors, " +
                "and your coding agent will follow them. Review them before relying on them.");
        }
    }

    private void WriteContext(InstallResult result)
    {
        output.WriteLine($"Target:      {result.Target ?? "(packages named on the command line)"}");
        output.WriteLine($"NuGet cache: {result.GlobalPackagesFolder}");
        output.WriteLine($"Destination: {result.Destination}");

        var scope = result.Target is null ? "named explicitly" : "direct";

        output.WriteLine($"Scanned {Count(result.PackagesScanned, "package")} ({scope}).");
        output.WriteLine();
    }

    private void WriteSkipped(InstallResult result)
    {
        if (result.Skipped.Count == 0)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine($"Warning: skipped {Count(result.Skipped.Count, "colliding skill")}:");

        foreach (var skill in result.Skipped)
        {
            output.WriteLine($"  {Describe(skill.RelativePath, skill.PackageId, skill.PackageVersion)}");
            output.WriteLine($"      {skill.Reason}");
        }
    }

    private void WriteNotOnDisk(InstallResult result)
    {
        if (result.NotOnDisk.Count == 0)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine(
            $"{Count(result.NotOnDisk.Count, "package")} resolved but not extracted in the NuGet cache. " +
            "Run 'dotnet restore' and try again:");

        foreach (var package in result.NotOnDisk)
        {
            output.WriteLine($"  {package}");
        }
    }

    public void WriteUninstallReport(IReadOnlyList<TrackedSkill> removed, string destination, bool dryRun)
    {
        output.WriteLine($"Destination: {destination}");
        output.WriteLine();

        if (removed.Count == 0)
        {
            output.WriteLine("Nothing to remove. No skills installed by this tool were found there.");
            return;
        }

        output.WriteLine($"{(dryRun ? "Would remove" : "Removed")} {Count(removed.Count, "skill")}:");

        foreach (var entry in removed)
        {
            output.WriteLine($"  {Describe(entry.Skill, entry.Package, entry.Version)}");
        }
    }

    public void WriteError(string message)
    {
        Console.Error.WriteLine($"error: {message}");
    }

    /// <summary>
    /// Reported when the user leaves the interactive picker without confirming. Nothing failed,
    /// so this is a statement of fact rather than an error.
    /// </summary>
    public void WriteCancelled()
    {
        output.WriteLine("Cancelled. Nothing was copied or removed.");
    }

    private static string Count(int value, string noun) => $"{value} {noun}{(value == 1 ? string.Empty : "s")}";

    /// <summary>One skill on one line: the folder name, then who it came from.</summary>
    /// <remarks>
    /// This used to be two lines, with "from Package Version" indented underneath. That doubled
    /// the length of every report to carry a word — "from" — that the brackets say for free, and
    /// twelve skills read far more easily as twelve lines than as twenty-four.
    /// </remarks>
    private static string Describe(string skill, string package, string version) =>
        $"{skill} ({package} {version})";
}
