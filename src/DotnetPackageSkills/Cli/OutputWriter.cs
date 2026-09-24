using DotnetPackageSkills.Skills;

namespace DotnetPackageSkills.Cli;

/// <summary>Renders results for people. There is no machine-readable report.</summary>
public sealed class OutputWriter(TextWriter output, TextWriter? errorOutput = null)
{
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
        else if (result.NothingNewToInstall)
        {
            // An interactive install lists only skills that are not installed. With nothing left
            // to list there was no checklist, and the skipped section below says why when some
            // skills could not be offered.
            output.WriteLine(result.Skipped.Count == 0
                ? "Nothing new to install. Every skill that these packages ship is already installed."
                : "Nothing new to install.");
        }
        else if (result.SkillsDiscovered > 0)
        {
            // Packages did ship skills; none of them ended up installed, because they were
            // deselected or skipped. Saying nobody ships a skill here would be a lie, and the
            // sections below already explain what happened to each one.
            output.WriteLine($"{verb} no skills.");
        }
        else
        {
            // Packages missing from the cache look exactly like packages without skills, so
            // claim nothing about why the list is empty.
            output.WriteLine("No bundled skills found.");
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

        WriteUnreferenced(result);
        WriteSkipped(result);

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
        output.WriteLine($"Target:      {TerminalText.Sanitize(result.Target ?? "(packages named on the command line)")}");
        output.WriteLine($"NuGet cache: {TerminalText.Sanitize(result.GlobalPackagesFolder)}");
        output.WriteLine($"Destination: {TerminalText.Sanitize(result.Destination)}");

        var scope = result.Target is null ? "named explicitly" : "direct";

        output.WriteLine($"Scanned {Count(result.PackagesScanned, "package")} ({scope}).");
        output.WriteLine();
    }

    /// <summary>
    /// Install never removes a skill because its package left the project, so say which ones
    /// stayed and which command removes them.
    /// </summary>
    private void WriteUnreferenced(InstallResult result)
    {
        if (result.Unreferenced.Count == 0)
        {
            return;
        }

        var one = result.Unreferenced.Count == 1;
        var packages = result.Unreferenced
            .Select(entry => entry.Package)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == 1
            ? "a package"
            : "packages";

        output.WriteLine();
        output.WriteLine(
            $"{Count(result.Unreferenced.Count, "installed skill")} {(one ? "belongs" : "belong")} to " +
            $"{packages} that the target no longer references:");

        foreach (var entry in result.Unreferenced)
        {
            output.WriteLine($"  {Describe(entry.Skill, entry.Package, entry.Version)}");
        }

        output.WriteLine(
            $"Run '{TerminalText.Sanitize(result.StaleCommand)}' to remove {(one ? "it" : "them")}.");
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
            output.WriteLine($"      {TerminalText.Sanitize(skill.Reason)}");
        }
    }

    /// <param name="target">
    /// The solution or project that <c>uninstall --stale</c> compared against, or null for a
    /// plain uninstall.
    /// </param>
    public void WriteUninstallReport(
        IReadOnlyList<TrackedSkill> removed,
        string destination,
        bool dryRun,
        string? target = null)
    {
        if (target is not null)
        {
            output.WriteLine($"Target:      {TerminalText.Sanitize(target)}");
        }

        output.WriteLine($"Destination: {TerminalText.Sanitize(destination)}");
        output.WriteLine();

        if (removed.Count == 0)
        {
            output.WriteLine(target is null
                ? "Nothing to remove. No skills installed by this tool were found there."
                : "Nothing to remove. No stale skills were found.");
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
        var text = TerminalText.Sanitize(message, multiline: true).Replace("\n", Environment.NewLine);
        (errorOutput ?? Console.Error).WriteLine($"error: {text}");
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
    /// Sanitize fields separately so an unterminated control in one cannot hide the next.
    /// </remarks>
    private static string Describe(string skill, string package, string version) =>
        $"{TerminalText.Sanitize(skill)} ({TerminalText.Sanitize(package)} {TerminalText.Sanitize(version)})";
}
