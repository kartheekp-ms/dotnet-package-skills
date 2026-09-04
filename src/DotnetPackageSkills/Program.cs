using System.CommandLine;
using DotnetPackageSkills;
using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Infrastructure;
using DotnetPackageSkills.NuGet;

return CommandLineBuilder.Build().Parse(args).Invoke();

namespace DotnetPackageSkills.Cli
{
    /// <summary>Wires up the command line surface.</summary>
    internal static class CommandLineBuilder
    {
        /// <summary>
        /// Vendor-neutral default. Agents that follow another convention are one
        /// --destination away, which is why this is a default rather than a hard-coded path.
        /// </summary>
        private const string DefaultDestination = ".agents/skills";

        public static RootCommand Build()
        {
            var target = new Option<string?>("--target", "-t")
            {
                Description = "Solution or project to inspect. Defaults to searching the current directory.",
                HelpName = "PATH",
            };

            var package = new Option<string[]>("--package", "-p")
            {
                Description =
                    "Take skills from an exact package instead of a project, as Id@Version " +
                    "(for example Mockly@1.10.0). Repeatable. Floating versions are not accepted.",
                HelpName = "ID@VERSION",
                Arity = ArgumentArity.OneOrMore,
                AllowMultipleArgumentsPerToken = true,
            };

            var destination = new Option<string>("--destination", "-d")
            {
                Description = $"Folder to copy skills into. Default: {DefaultDestination}",
                HelpName = "PATH",
                DefaultValueFactory = _ => DefaultDestination,
            };

            var noRestore = new Option<bool>("--no-restore")
            {
                Description = "Fail instead of restoring when the target has not been restored yet.",
            };

            var globalPackages = new Option<string?>("--global-packages")
            {
                Description = "Override the NuGet global packages folder instead of asking the CLI.",
                HelpName = "PATH",
            };

            var dryRun = new Option<bool>("--dry-run")
            {
                Description = "Report what would change without writing anything.",
            };

            var json = new Option<bool>("--json")
            {
                Description = "Emit machine-readable JSON instead of the human-readable report.",
            };

            var interactive = new Option<bool>("--interactive", "-i")
            {
                Description =
                    "Choose which discovered skills to install, one page at a time. Skills already " +
                    "installed start selected; turning one off removes it.",
            };

            var uninstallPackage = new Option<string?>("--package", "-p")
            {
                Description =
                    "Remove only skills from this package. Accepts Id to remove every version, " +
                    "or Id@Version to remove one.",
                HelpName = "ID[@VERSION]",
            };

            // Its own option rather than the one install uses, because "copy skills into" is
            // nonsense on a command that only deletes. It still has to exist: skills installed
            // to somewhere other than the default are unreachable without it.
            var uninstallDestination = new Option<string>("--destination", "-d")
            {
                Description = $"Folder to remove skills from. Default: {DefaultDestination}",
                HelpName = "PATH",
                DefaultValueFactory = _ => DefaultDestination,
            };

            var install = new Command("install", "Copy skills bundled in NuGet packages into the repository.")
            {
                target, package, destination, noRestore, globalPackages, dryRun, json, interactive,
            };
            install.Validators.Add(RejectTargetWithPackage);
            install.Validators.Add(RejectInteractiveWithJson);
            install.SetAction(parseResult => Run(() =>
            {
                var request = BuildRequest(parseResult);
                var service = new SkillInstallService(new ProcessRunner());

                var result = parseResult.GetValue(interactive)
                    ? InstallInteractively(service, request)
                    : service.Install(request);

                if (result is null)
                {
                    new OutputWriter(Console.Out).WriteCancelled();
                    return;
                }

                Report(
                    parseResult,
                    writer => writer.WriteInstallReport(result, copied: true),
                    JsonReport.For(result));
            }));

            var list = new Command("list", "Show which packages ship skills, without copying anything.")
            {
                target, package, destination, noRestore, globalPackages, json,
            };
            list.Validators.Add(RejectTargetWithPackage);
            list.SetAction(parseResult => Run(() =>
            {
                var request = BuildRequest(parseResult) with { DryRun = true };
                var result = new SkillInstallService(new ProcessRunner()).Discover(request);
                Report(
                    parseResult,
                    writer => writer.WriteInstallReport(result, copied: false),
                    JsonReport.For(result));
            }));

            var uninstallInteractive = new Option<bool>("--interactive", "-i")
            {
                Description =
                    "Choose which installed skills to remove, one page at a time. " +
                    "Only skills this tool installed are listed.",
            };

            var uninstall = new Command("uninstall", "Remove skills this tool previously copied in.")
            {
                uninstallDestination, uninstallPackage, dryRun, json, uninstallInteractive,
            };
            uninstall.Validators.Add(result =>
            {
                if (result.GetResult(uninstallInteractive) is not null && result.GetResult(json) is not null)
                {
                    result.AddError(
                        "--interactive and --json cannot be combined. JSON output is for scripts, " +
                        "and a script has nobody to answer the prompt.");
                }
            });
            uninstall.SetAction(parseResult => Run(() =>
            {
                var workingDirectory = Directory.GetCurrentDirectory();
                var destinationValue = parseResult.GetValue(uninstallDestination) ?? DefaultDestination;
                var isDryRun = parseResult.GetValue(dryRun);
                var (id, version) = ParseUninstallFilter(parseResult.GetValue(uninstallPackage));
                var root = Path.GetFullPath(destinationValue, workingDirectory);

                IReadOnlyCollection<string>? chosen = null;

                if (parseResult.GetValue(uninstallInteractive))
                {
                    chosen = ChooseWhatToRemove(destinationValue, workingDirectory, id, version);

                    if (chosen is null)
                    {
                        new OutputWriter(Console.Out).WriteCancelled();
                        return;
                    }
                }

                var removed = new SkillInstallService(new ProcessRunner())
                    .Uninstall(destinationValue, workingDirectory, id, version, isDryRun, chosen);

                Report(
                    parseResult,
                    writer => writer.WriteUninstallReport(removed, root, isDryRun),
                    JsonReport.ForUninstall(removed, root, isDryRun));
            }));

            return new RootCommand(
                """
                Copies agent skills bundled inside NuGet packages into a folder your coding agent reads.

                Package authors ship skills at skills/<package-id>-<skill-name>/SKILL.md inside the package. Restore extracts them to the NuGet global packages folder, which is outside your repository and which no coding agent scans. This tool bridges that gap.
                """)
            {
                install, list, uninstall,
            };

            InstallRequest BuildRequest(ParseResult parseResult) => new()
            {
                Target = parseResult.GetValue(target),
                Packages = [.. (parseResult.GetValue(package) ?? []).Select(PackageCoordinate.Parse)],
                Destination = parseResult.GetValue(destination) ?? DefaultDestination,
                WorkingDirectory = Directory.GetCurrentDirectory(),
                AllowRestore = !parseResult.GetValue(noRestore),
                GlobalPackagesOverride = parseResult.GetValue(globalPackages),
                DryRun = parseResult.GetValue(dryRun),
            };

            void RejectTargetWithPackage(System.CommandLine.Parsing.CommandResult result)
            {
                // Both would answer "which packages", and combining them hides which one won.
                if (result.GetResult(target) is not null && result.GetResult(package) is not null)
                {
                    result.AddError(
                        "--target and --package cannot be combined. Use --target to take versions " +
                        "from a project, or --package to name exact packages yourself.");
                }
            }

            void RejectInteractiveWithJson(System.CommandLine.Parsing.CommandResult result)
            {
                if (result.GetResult(interactive) is not null && result.GetResult(json) is not null)
                {
                    result.AddError(
                        "--interactive and --json cannot be combined. JSON output is for scripts, " +
                        "and a script has nobody to answer the prompt.");
                }
            }

            void Report(ParseResult parseResult, Action<OutputWriter> writeReport, object jsonPayload)
            {
                var writer = new OutputWriter(Console.Out);

                if (parseResult.GetValue(json))
                {
                    writer.WriteJson(jsonPayload);
                }
                else
                {
                    writeReport(writer);
                }
            }
        }

        /// <summary>
        /// Discovers skills, lets the user pick from them a page at a time, then installs the
        /// selection. Returns null when the user cancelled.
        /// </summary>
        private static InstallResult? InstallInteractively(SkillInstallService service, InstallRequest request)
        {
            var discovered = service.Discover(request);

            // Nothing to choose between, so there is no prompt to show. Install anyway, because
            // pruning still has work to do when a package stopped shipping a skill.
            if (discovered.Skills.Count == 0)
            {
                return service.Install(request, discovered, choice: null);
            }

            var installed = SkillInstallService.InstalledSkillNames(discovered.Destination);

            var items = discovered.Skills
                .Select(skill => new SkillPickerItem(
                    skill.RelativePath,
                    skill.PackageId,
                    skill.PackageVersion,
                    installed.Contains(skill.RelativePath)))
                .ToList();

            var picked = new SkillPicker(new SystemTerminal()).Choose(items, PickerTitle(discovered));

            if (picked is null)
            {
                return null;
            }

            // A tick keeps the skill. Anything already installed that is no longer ticked is a
            // deliberate removal, which is not the same as a skill simply going unmentioned.
            var choice = new SkillChoice(
                [.. discovered.Skills.Where(skill => picked.Contains(skill.RelativePath))],
                [.. installed.Where(name => !picked.Contains(name))]);

            return service.Install(request, discovered, choice);
        }

        /// <summary>
        /// Offers the installed skills for removal and returns the ones ticked, or null when
        /// the user cancelled.
        /// </summary>
        /// <remarks>
        /// The list comes from the manifest, so it holds exactly what this tool put there and
        /// nothing a user wrote themselves. An empty list still returns an empty selection
        /// rather than prompting, so the report can say there was nothing to remove.
        /// </remarks>
        private static IReadOnlyCollection<string>? ChooseWhatToRemove(
            string destination,
            string workingDirectory,
            string? packageId,
            string? packageVersion)
        {
            var installed = SkillInstallService.InstalledSkills(destination, workingDirectory)
                .Where(entry => packageId is null ||
                                entry.Package.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                .Where(entry => packageVersion is null || entry.Version == packageVersion)
                .ToList();

            if (installed.Count == 0)
            {
                return [];
            }

            var items = installed
                .Select(entry => new SkillPickerItem(entry.Skill, entry.Package, entry.Version, Installed: true))
                .ToList();

            return new SkillPicker(new SystemTerminal())
                .Choose(items, "Installed skills", PickerMode.Uninstall)
                ?.ToList();
        }

        private static string PickerTitle(InstallResult discovered) =>
            discovered.Target is null
                ? "Skills from the packages you named"
                : $"Skills for {Path.GetFileName(discovered.Target)}";

        /// <summary>
        /// Splits the uninstall filter, which unlike --package on install may omit the version
        /// to mean "every version of this package".
        /// </summary>
        private static (string? Id, string? Version) ParseUninstallFilter(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return (null, null);
            }

            if (!value.Contains(PackageCoordinate.Separator))
            {
                return (value.Trim(), null);
            }

            var coordinate = PackageCoordinate.Parse(value);
            return (coordinate.Id, coordinate.Version);
        }

        /// <summary>
        /// Turns expected failures into a plain message and a non-zero exit code. Users of a CLI
        /// should get guidance, not a stack trace, for anything we anticipated.
        /// </summary>
        private static int Run(Action action)
        {
            try
            {
                action();
                return 0;
            }
            catch (Exception ex) when (ex is PackageSkillsException or ProcessExecutionException)
            {
                new OutputWriter(Console.Out).WriteError(ex.Message);
                return 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                new OutputWriter(Console.Out).WriteError(
                    $"{ex.Message}{Environment.NewLine}" +
                    "Check that the destination folder is writable and not open in another program.");
                return 1;
            }
        }
    }
}
