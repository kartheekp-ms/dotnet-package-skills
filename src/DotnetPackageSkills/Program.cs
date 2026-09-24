using System.CommandLine;
using DotnetPackageSkills;
using DotnetPackageSkills.Cli;
using DotnetPackageSkills.Infrastructure;
using DotnetPackageSkills.NuGet;
using DotnetPackageSkills.Skills;

return CommandLineBuilder.Invoke(args);

namespace DotnetPackageSkills.Cli
{
    /// <summary>Wires up the command line surface.</summary>
    internal static class CommandLineBuilder
    {
        /// <summary>
        /// Vendor-neutral default. Agents that follow another convention are one
        /// --destination away, which is why this is a default rather than a hard-coded path.
        /// </summary>
        private const string DefaultDestination = InstallRequest.DefaultDestination;

        public static int Invoke(string[] args, TextWriter? output = null, TextWriter? error = null) =>
            CommandLineDiagnostics.Invoke(Build().Parse(args), output ?? Console.Out, error ?? Console.Error);

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
            package.Validators.Add(result =>
            {
                // Taking several values means the parser hands --package any unknown option that
                // follows it, such as a --json left in an old script. No package ID starts with
                // '-', so report it the way the parser reports an unknown option anywhere else.
                foreach (var token in result.Tokens.Where(token => token.Value.StartsWith('-')))
                {
                    result.AddError($"Unrecognized command or argument '{token.Value}'.");
                }
            });

            var destination = new Option<string>("--destination", "-d")
            {
                Description = $"Folder to copy skills into. Default: {DefaultDestination}",
                HelpName = "PATH",
                DefaultValueFactory = _ => DefaultDestination,
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

            var interactive = new Option<bool>("--interactive", "-i")
            {
                Description =
                    "Choose which skills to add, with descriptions, one page at a time. " +
                    "Only skills that aren't installed are listed; installed skills are left as they are.",
            };

            var uninstallPackage = new Option<string?>("--package", "-p")
            {
                Description =
                    "Remove only this package's skills. Accepts Id, or Id@Version to remove them " +
                    "only if that version is the one installed.",
                HelpName = "ID[@VERSION]",
                Arity = ArgumentArity.ExactlyOne,
            };
            uninstallPackage.Validators.Add(result =>
            {
                if (result.IdentifierTokenCount > 1)
                {
                    result.AddError("--package can be specified only once for uninstall.");
                    return;
                }

                if (result.Tokens.Count == 1)
                {
                    try
                    {
                        ParseUninstallFilter(result.Tokens[0].Value);
                    }
                    catch (PackageSkillsException error)
                    {
                        result.AddError(error.Message);
                    }
                }
            });

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
                target, package, destination, globalPackages, dryRun, interactive,
            };
            install.Validators.Add(RejectTargetWithPackage);
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

                new OutputWriter(Console.Out).WriteInstallReport(result, copied: true);
            }));

            var list = new Command("list", "Show which packages ship skills, without copying anything.")
            {
                target, package, destination, globalPackages,
            };
            list.Validators.Add(RejectTargetWithPackage);
            list.SetAction(parseResult => Run(() =>
            {
                var request = BuildRequest(parseResult) with { DryRun = true };
                var result = new SkillInstallService(new ProcessRunner()).Discover(request);
                new OutputWriter(Console.Out).WriteInstallReport(result, copied: false);
            }));

            var uninstallInteractive = new Option<bool>("--interactive", "-i")
            {
                Description =
                    "Choose which installed skills to remove, with descriptions, one page at a time. " +
                    "Only skills this tool installed are listed.",
            };

            var stale = new Option<bool>("--stale")
            {
                Description =
                    "Remove only stale skills: skills whose package the target no longer references, " +
                    "or references at a different version. Reads the target's package references, " +
                    "so it needs a solution or project.",
            };

            var staleTarget = new Option<string?>("--target", "-t")
            {
                Description =
                    "With --stale, the solution or project to compare against. " +
                    "Defaults to searching the current directory.",
                HelpName = "PATH",
            };

            var uninstall = new Command("uninstall", "Remove skills this tool previously copied in.")
            {
                uninstallDestination, uninstallPackage, stale, staleTarget, dryRun, uninstallInteractive,
            };
            uninstall.Validators.Add(result =>
            {
                var isStale = result.GetResult(stale) is not null;

                if (isStale && result.GetResult(uninstallPackage) is not null)
                {
                    result.AddError(
                        "--stale and --package cannot be combined. --stale removes the skills that no longer " +
                        "match the target; --package removes one package's skills.");
                }

                if (!isStale && result.GetResult(staleTarget) is not null)
                {
                    result.AddError("--target can be used with uninstall only together with --stale.");
                }
            });
            uninstall.SetAction(parseResult => Run(() =>
            {
                var workingDirectory = Directory.GetCurrentDirectory();
                var destinationValue = parseResult.GetValue(uninstallDestination) ?? DefaultDestination;
                var isDryRun = parseResult.GetValue(dryRun);
                var (id, version) = ParseUninstallFilter(parseResult.GetValue(uninstallPackage));
                var root = Path.GetFullPath(destinationValue, workingDirectory);
                var service = new SkillInstallService(new ProcessRunner());
                var references = parseResult.GetValue(stale)
                    ? service.ReadReferences(parseResult.GetValue(staleTarget), workingDirectory)
                    : null;

                UninstallChoice? choice = null;

                if (parseResult.GetValue(uninstallInteractive))
                {
                    choice = ChooseWhatToRemove(destinationValue, workingDirectory, id, version, references);

                    if (choice is null)
                    {
                        new OutputWriter(Console.Out).WriteCancelled();
                        return;
                    }
                }

                var removed = service.Uninstall(destinationValue, workingDirectory, id, version, isDryRun,
                    choice?.Selected, choice?.ExpectedInstalled, references?.Packages);

                new OutputWriter(Console.Out).WriteUninstallReport(removed, root, isDryRun, references?.Target);
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
        }

        /// <summary>
        /// Discovers skills, lets the user pick from the ones not installed yet a page at a time,
        /// then copies the picks. Returns null when the user cancelled.
        /// </summary>
        /// <remarks>
        /// Every check that could stop the install runs before the checklist opens, so a choice is
        /// never made only to be refused. With nothing new to offer there is no checklist at all.
        /// </remarks>
        private static InstallResult? InstallInteractively(SkillInstallService service, InstallRequest request)
        {
            var discovered = service.Discover(request);
            var installed = SkillInstallService.InstalledSkills(discovered.Destination, request.WorkingDirectory);
            var prepared = service.PrepareInteractiveInstall(request, discovered, installed);
            var items = InteractiveSkills.ForInstall(prepared.Skills, installed);

            if (items.Count == 0)
            {
                return prepared with { NothingNewToInstall = prepared.SkillsDiscovered > 0 };
            }

            var picked = new SkillPicker(new SystemTerminal())
                .Choose(items, PickerTitle(discovered), PickerMode.Install, InstalledSkillsNote);

            if (picked is null)
            {
                return null;
            }

            var choice = InteractiveSkills.InstallChoice(prepared.Skills, installed, items, picked);

            return service.Install(request, prepared, choice);
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
        private static UninstallChoice? ChooseWhatToRemove(
            string destination,
            string workingDirectory,
            string? packageId,
            string? packageVersion,
            TargetReferences? references)
        {
            var installed = SkillInstallService.InstalledSkills(destination, workingDirectory);
            var matching = installed
                .Where(entry => SkillInstaller.Matches(entry, packageId, packageVersion))
                .Where(entry => references is null || SkillInstaller.IsStale(entry, references.Packages))
                .ToList();

            if (matching.Count == 0)
            {
                return new UninstallChoice([], installed);
            }

            var items = InteractiveSkills.ForUninstall(
                matching,
                Path.GetFullPath(destination, workingDirectory));

            var selected = new SkillPicker(new SystemTerminal()).Choose(
                items,
                "Which skills should be uninstalled?",
                PickerMode.Uninstall,
                references is null ? null : StaleSkillsNote);
            return selected is null ? null : new UninstallChoice(selected.ToList(), installed);
        }

        /// <summary>Shown under the install checklist title, because the list is not everything.</summary>
        internal const string InstalledSkillsNote = "Installed skills aren't listed.";

        /// <summary>Shown under the uninstall checklist title with <c>--stale</c>.</summary>
        internal const string StaleSkillsNote = "Only skills that don't match the target are listed.";

        private static string PickerTitle(InstallResult discovered) =>
            discovered.Target is null
                ? "Which skills should be installed?"
                : $"Which skills should be installed? ({Path.GetFileName(discovered.Target)})";

        /// <summary>
        /// Splits the uninstall filter, which unlike --package on install may omit the version
        /// to mean "whichever version of this package is installed".
        /// </summary>
        internal static (string? Id, string? Version) ParseUninstallFilter(string? value)
        {
            if (value is null)
            {
                return (null, null);
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new PackageSkillsException(
                    "--package requires a non-empty package ID, optionally followed by @Version. " +
                    "Omit --package only when you intend to remove all tracked skills.");
            }

            if (!value.Contains(PackageCoordinate.Separator))
            {
                var id = value.Trim();
                PackageCoordinate.ValidateId(id);
                return (id, null);
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
