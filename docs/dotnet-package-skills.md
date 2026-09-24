---
title: dotnet package-skills command
description: The 'dotnet package-skills' command discovers agent skills bundled in NuGet packages and copies the skills you choose into a repository.
ms.date: 09/23/2026
---
# dotnet package-skills

**This article applies to:** ✔️ `dotnet-package-skills` version 0.1.0

## Name

`dotnet package-skills` - Discovers, installs, and removes agent skills bundled in NuGet packages.

> [!NOTE]
> `dotnet package-skills` is provided by the `dotnet-package-skills` .NET tool, not by the .NET SDK. After the tool is installed, `dotnet package-skills` and `dotnet-package-skills` are equivalent. For example, to install version 0.1.0 from a local package feed, run `dotnet tool install --global --add-source <FEED> dotnet-package-skills --version 0.1.0`.

## Synopsis

```dotnetcli
dotnet package-skills install [-d|--destination <PATH>] [--dry-run]
    [--global-packages <PATH>] [-i|--interactive] [--no-restore]
    [-p|--package <ID@VERSION>...] [-t|--target <PATH>]

dotnet package-skills list [-d|--destination <PATH>] [--global-packages <PATH>]
    [--no-restore] [-p|--package <ID@VERSION>...] [-t|--target <PATH>]

dotnet package-skills uninstall [-d|--destination <PATH>] [--dry-run]
    [-i|--interactive] [-p|--package <ID[@VERSION]>]

dotnet package-skills uninstall --stale [-d|--destination <PATH>] [--dry-run]
    [-i|--interactive] [--no-restore] [-t|--target <PATH>]

dotnet package-skills [install|list|uninstall] -h|--help

dotnet package-skills --version
```

## Description

The `dotnet package-skills` command makes agent skills that package authors ship in NuGet packages available in your repository, where coding agents can find them. A skill is an immediate subfolder of a package's `skills` folder that contains a `SKILL.md` file. The command copies each selected skill folder, including its supporting files, from the NuGet global packages folder into a skills destination. The default destination is `.agents/skills` under the current directory. Use `--destination` for another location, such as `.claude/skills`. Whether an agent reads a destination is up to that agent.

The command reads packages that are already extracted in the NuGet global packages folder. It doesn't download packages, move or modify package contents, change project package references, install an agent, or configure an MCP server. Only the target's direct package references are scanned.

The tool requires the .NET 8 runtime or later; current builds target .NET 8 and .NET 10. Project discovery also uses the installed .NET SDK.

### Discover skills

`dotnet package-skills list` reports the skills available from packages without copying anything. It doesn't read or change the destination, so it isn't an inventory of installed skills.

Without `--target`, the command searches the current directory. It prefers a solution in that directory, then a project in that directory, then a solution in a subdirectory, and then a project in a subdirectory. A `.slnx` file is preferred over a `.sln` file, and matches under `bin`, `obj`, `.git`, `node_modules`, and `artifacts` directories are ignored. When several files match, the choice is deterministic, and the report shows the selected file after `Target:`. Specify `--target` when the choice matters.

The following example shows the output of the `dotnet package-skills list` command for a solution whose direct package references ship four skills:

```output
Target:      C:\src\MyApp\MyApp.slnx
NuGet cache: C:\packages
Destination: C:\src\MyApp\.agents\skills
Scanned 2 packages (direct).

Found 4 skills:
  contoso.widgets-widget-testing (Contoso.Widgets 2.3.0)
  contoso.widgets-widget-usage (Contoso.Widgets 2.3.0)
  fabrikam.testing-fakes (Fabrikam.Testing 1.4.0)
  fabrikam.testing-fixtures (Fabrikam.Testing 1.4.0)
```

Reading the target's package list can restore the target. To prevent restore, use `--no-restore`. The NuGet global packages folder must already exist; if it doesn't, restore the target first. `list` skips packages that aren't extracted in the NuGet global packages folder.

Packages named with `--package` must already be extracted in the NuGet global packages folder. Naming a package doesn't download it or add it to a project. These reports show `Target: (packages named on the command line)` and `Scanned N packages (named explicitly).`

### Install and refresh skills

`dotnet package-skills install` copies the discovered skills into the destination and records them in an ownership manifest named `.dotnet-package-skills.json` in the destination. Each skill keeps its authored folder name. Running `install` again refreshes installed skills; there's no separate update command. Refreshing a skill that the tool installed replaces that skill's folder, including local edits and added files, each time `install` includes the skill. Keep your own guidance in separate skill folders that the tool doesn't track.

The report starts with the same `Target`, `NuGet cache`, `Destination`, and `Scanned` lines as `list`. The following example shows the rest of the report:

```output
Copied 4 skills:
  contoso.widgets-widget-testing (Contoso.Widgets 2.3.0)
  contoso.widgets-widget-usage (Contoso.Widgets 2.3.0)
  fabrikam.testing-fakes (Fabrikam.Testing 1.4.0)
  fabrikam.testing-fixtures (Fabrikam.Testing 1.4.0)

These skills are instructions written by the package authors, and your coding agent will follow them. Review them before relying on them.
```

What `install` removes depends on how it runs:

| Scope | Behavior |
| --- | --- |
| Any noninteractive run | For each package found in the NuGet global packages folder at a version other than the installed one, refreshes the package's skills and removes the installed ones that the new version doesn't ship. A version that isn't in the folder never causes a removal. |
| Target | Keeps the skills of packages that the target no longer references, and lists them with a pointer to `uninstall --stale`. |
| `--package` | Leaves the skills of packages it doesn't name in place, without a report. |
| `--interactive` | Only adds skills that aren't installed. It never refreshes or removes skills. |

The following example shows the end of a target report after a package left the project:

```output
2 installed skills belong to a package that the target no longer references:
  fabrikam.testing-fakes (fabrikam.testing 1.4.0)
  fabrikam.testing-fixtures (fabrikam.testing 1.4.0)
Run 'dotnet package-skills uninstall --stale' to remove them.
```

Installed skills are listed by the lowercase package ID that the manifest records. The suggested command repeats the `--target` and `--destination` that you passed, so it can be run as printed; so do the commands that errors suggest.

If any resolved package of the target is missing from the NuGet global packages folder, `install` fails before changing any skill or the manifest, including with `--interactive` or `--dry-run`. Restore the target into the same folder, and then retry.

The destination holds skills from one version of each package. If the target resolves more than one version of a package, or `--package` names more than one, `install` fails before changing anything and names the versions:

```output
error: Cannot install skills because these packages resolve to more than one version: Contoso.Widgets (2.2.0, 2.3.0). Skills can come from only one version of each package. Align the versions, for example with Central Package Management, and then try again. No skills were changed.
```

[Central Package Management](https://learn.microsoft.com/nuget/consume-packages/central-package-management) keeps the projects of a repository on one version of each package. Equivalent versions, such as `1.10` and `1.10.0`, count as one version. `list` still shows every version.

Destination names are compared case-insensitively. If several packages provide the same skill folder name, the first one in deterministic package order is copied, and the others are skipped with a warning. If a different package already owns an installed name, the existing owner is preserved in every mode. Uninstall that skill before installing a replacement. If the current owner's package is also a candidate, its skill is refreshed. If the owner's package moves to a version that no longer ships the skill while another package ships a skill with that name, `install` fails before changing anything: removing the old copy would hand the name over, and the manifest can't record it under the new version. Run the `uninstall --package` command that the error suggests, and then install again. An existing folder that the manifest doesn't track is treated as yours and is never overwritten.

To preview the changes, add `--dry-run`:

```output
Would copy 4 skills:
  contoso.widgets-widget-testing (Contoso.Widgets 2.3.0)
  contoso.widgets-widget-usage (Contoso.Widgets 2.3.0)
  fabrikam.testing-fakes (Fabrikam.Testing 1.4.0)
  fabrikam.testing-fixtures (Fabrikam.Testing 1.4.0)
```

Planned removals appear under `Would remove`. A dry run doesn't copy or delete skills or create or update the manifest. It doesn't prevent restore; use `--no-restore` for that.

### Choose skills interactively

With `--interactive`, `install` and `uninstall` open a paged checklist. The install checklist lists only the skills that aren't installed, and nothing starts checked. The following example shows an install page after a selection is made:

```output
Which skills should be installed? (MyApp.slnx)
Installed skills aren't listed.

> [X] contoso.widgets-widget-usage - Correct usage patterns for the
      Contoso.Widgets library, including lifetime rules and the batching API.
      Use whenever code creates, configures, or disposes a Widget.
  [ ] fabrikam.testing-fakes - Create fakes and verify their calls in unit
      tests.
  [ ] fabrikam.testing-fixtures - Share expensive setup across tests with
      fixtures.

1 of 3 selected
(Press <space> to select, <enter> to accept)
(Press <up>/<down> to move, <Home>/<End> for first/last)
(Press <a> to select all, <c> to clear all, <Esc>/<q>/<Ctrl+C> to cancel)
Blue X: selected
```

In this example, `contoso.widgets-widget-testing` is already installed, so it isn't listed. Accepting copies only the checked skills. An interactive install never refreshes or removes skills; run `install` without `--interactive` to refresh them, and `uninstall` to remove them. Candidates that would be skipped, such as a name that another package or an untracked folder already uses, aren't listed and appear in the report as skipped. When every discovered skill is installed, the command doesn't open the checklist:

```output
Nothing new to install. Every skill that these packages ship is already installed.
```

Before the checklist opens, an interactive install fails without changing anything in these cases:

| Case | Resolution |
| --- | --- |
| The target resolves more than one version of a package, or `--package` names more than one. | Align the versions, or name one version per package. |
| With a target, a resolved package is missing from the NuGet global packages folder. | Restore the target. |
| With a target, an installed skill is stale: the target no longer references its package, or references a different version. | Run `dotnet package-skills uninstall --stale`. |
| With `--package`, a named package is installed at another version. | Run `install --package` without `--interactive` to change the version, or `uninstall --package <ID>` first. |

Skills installed with `install --package` from packages that the target doesn't reference count as stale for target commands.

Each row shows `skill-name - description`. Descriptions come from the top-level YAML `description` in each `SKILL.md` file: `install` reads package copies, and `uninstall` reads installed copies. A missing description shows `No description provided.`, and unreadable or invalid metadata shows a warning without preventing selection. Descriptions wrap to the terminal width, and pages are sized by rendered lines. Resizing the terminal reflows the list while keeping focus and selections. The line under the title says what the list leaves out; it's omitted when the window is too small to fit it.

| Visual cue | Meaning |
| --- | --- |
| Blue row text | Keyboard focus, including wrapped description lines. |
| Blue `X` | Checked: install in the install picker, or remove in the uninstall picker. |
| Red `[` and `]` | Marked for removal in the uninstall picker. |
| `+` or `-` | Marked for installation or removal when color is disabled, for example by `NO_COLOR`. |

| Key | Action |
| --- | --- |
| Up, Down | Move between skills, wrapping at the first and last skill. |
| Left, Right, PageUp, PageDown | Move between pages. |
| Home, End | Go to the first or last skill. |
| Space | Check or uncheck the focused skill. |
| A, C | Check all or clear all, across all pages. |
| Ctrl+Up, Ctrl+Down | Scroll a description that's too long for the page. |
| Enter | Accept. With `--dry-run`, only report. |
| Esc, Q, Ctrl+C | Cancel without changing any skills. |

The checklist is drawn at the top of a temporary terminal screen. When you accept or cancel, or when a handled error occurs, the original shell screen is restored and receives the report, so the checklist doesn't remain in scrollback. If the terminal is too small, the command fails with exit code 1 and asks you to enlarge the window; no skills are changed. If installed skill ownership changes while the picker is open, accepting fails without applying the stale selection. Cancelling prints:

```output
Cancelled. Nothing was copied or removed.
```

### Remove skills

`dotnet package-skills uninstall` removes skills that the manifest in the destination tracks. It doesn't remove NuGet packages or skills you wrote, and it doesn't need a project or the NuGet global packages folder. To remove skills from another destination, specify the same `--destination` that was used to install them.

```output
Destination: C:\src\MyApp\.agents\skills

Removed 2 skills:
  contoso.widgets-widget-testing (contoso.widgets 2.3.0)
  contoso.widgets-widget-usage (contoso.widgets 2.3.0)
```

`--package <ID>` removes the package's tracked skills, and `--package <ID@VERSION>` removes them only if that version is the one installed. The manifest records one version per package, so there's never more than one version to choose from. Package IDs match case-insensitively, and versions are compared in normalized form, so `1.10` matches `1.10.0`. A blank or missing filter value is an error rather than a request to remove everything, and `--package` can be specified only once.

The uninstall picker lists only tracked skills, and nothing starts checked. A check means remove:

```output
Which skills should be uninstalled?

> [X] contoso.widgets-widget-testing - Testing patterns for code that uses
      Contoso.Widgets. Use when writing unit or integration tests involving
      widgets.
  [ ] contoso.widgets-widget-usage - Correct usage patterns for the
      Contoso.Widgets library, including lifetime rules and the batching API.
      Use whenever code creates, configures, or disposes a Widget.

1 of 2 selected; 1 to remove
(Press <space> to select, <enter> to accept)
(Press <up>/<down> to move, <Home>/<End> for first/last)
(Press <a> to select all, <c> to clear all, <Esc>/<q>/<Ctrl+C> to cancel)
Blue X: selected Red brackets: remove
```

A tracked skill can be removed even if its installed `SKILL.md` file is missing. When nothing is tracked, the command succeeds without opening a picker:

```output
Destination: C:\src\MyApp\.agents\skills

Nothing to remove. No skills installed by this tool were found there.
```

### Remove stale skills

`install` never removes the skills of a package that the target stopped referencing. `uninstall --stale` removes every *stale* skill: a tracked skill whose package the target no longer references, or references at a different version than the manifest records. It reads the target's package references, so it needs a solution or project, found as described in [Discover skills](#discover-skills) or specified with `--target`. It needs only the package references, not the packages, so it doesn't look in the NuGet global packages folder for skills. It also works when the target resolves more than one version of a package: a skill is stale only if no project references its installed version.

```output
Target:      C:\src\MyApp\MyApp.slnx
Destination: C:\src\MyApp\.agents\skills

Removed 2 skills:
  fabrikam.testing-fakes (fabrikam.testing 1.4.0)
  fabrikam.testing-fixtures (fabrikam.testing 1.4.0)
```

With nothing stale, the command succeeds:

```output
Target:      C:\src\MyApp\MyApp.slnx
Destination: C:\src\MyApp\.agents\skills

Nothing to remove. No stale skills were found.
```

Add `--dry-run` to preview, or `--interactive` to choose among the stale skills:

```output
Which skills should be uninstalled?
Only skills that don't match the target are listed.

> [X] fabrikam.testing-fakes - Create fakes and verify their calls in unit
      tests.
  [ ] fabrikam.testing-fixtures - Share expensive setup across tests with
      fixtures.

1 of 2 selected; 1 to remove
(Press <space> to select, <enter> to accept)
(Press <up>/<down> to move, <Home>/<End> for first/last)
(Press <a> to select all, <c> to clear all, <Esc>/<q>/<Ctrl+C> to cancel)
Blue X: selected Red brackets: remove
```

`--stale` can't be combined with `--package`. `--target` and `--no-restore` are available for `uninstall` only together with `--stale`.

### Ownership manifest

The `.dotnet-package-skills.json` manifest records, for each package, the one version its skills came from and the skill folders it owns. Its shape follows the .NET local tool manifest, `dotnet-tools.json`:

```json
{
  "version": 1,
  "packages": {
    "contoso.widgets": {
      "version": "2.3.0",
      "skills": [
        "contoso.widgets-widget-testing",
        "contoso.widgets-widget-usage"
      ]
    }
  }
}
```

| Property | Meaning |
| --- | --- |
| `version` | The manifest format version. This release writes and reads `1`. |
| `packages` | One entry per package, keyed by the lowercase package ID. |
| `packages.<id>.version` | The package version that the skills were installed from. |
| `packages.<id>.skills` | The skill folders directly under the destination that the package owns. |

Removal acts only on the folders that the manifest lists, never on other contents of the destination. The manifest is written only after a successful installation or removal, not by `list`, a cancelled picker, or a dry run. When the last tracked skill is removed, the manifest is deleted, and the destination folder is deleted if it's empty. The file is written in UTF-8 without a byte order mark, with LF line endings and a stable order on every platform, so it can be committed without line-ending churn. Properties the tool doesn't recognize are ignored when the file is read and aren't preserved when it's rewritten.

If the manifest can't be read or is invalid, `install` and `uninstall` fail before changing anything and preserve the file. A manifest is invalid if it's malformed or has duplicate JSON properties, including properties that differ only in case; if it lacks the `version` or `packages` property; if a package ID is invalid or a package has no version; or if it claims a folder more than once or names an unsafe folder. Unsafe folder names include names that end in a dot or a space, such as `...`, because Windows can resolve them to another folder or to the destination itself. Resolve any merge conflict or restore the file from source control, and then retry. A manifest with a format version newer than `1` asks you to update the tool. A manifest written by a pre-release build of the tool, which has an `installed` array instead of `packages`, isn't converted; move the skills folder aside and install again. `list` still works because it doesn't read the manifest.

Concurrent invocations of the tool on the same destination are serialized, including equivalent Windows spellings of the destination path. A busy destination produces retry guidance instead of overlapping changes. Unexpected filesystem failures are reported, but a partially completed copy or removal isn't rolled back.

### Exit codes and errors

The command returns `0` for success, empty results, and cancellation, and `1` for failures. Warnings, such as skipped skills, can accompany exit code `0`; inspect the report when completeness matters. Errors are written to standard error, and argument errors can also print usage help to standard output. The reports are meant for people; scripts should rely on the exit code and read the ownership manifest for the installed skills.

Human-readable reports and diagnostics remove terminal escape sequences and other unsafe control characters from package metadata, paths, and messages. Arguments are validated as supplied, and stored identities are unchanged.

## Commands

- **`install`**

  Copies or refreshes skills from the target's direct package references, or from the packages named with `--package`, into the destination.

- **`list`**

  Lists the skills available from the target's direct package references, or from the packages named with `--package`, without copying anything.

- **`uninstall`**

  Removes skills that the tool installed in the destination. With `--stale`, removes only the skills that no longer match the target's package references.

## Options

- **`-d|--destination <PATH>`**

  The skills folder. Defaults to `.agents/skills`. A relative path is resolved from the current directory, not from the `--target` directory. For `list`, the destination only appears in the report; it isn't read or changed. To remove skills, specify the destination they were installed to. Available for `install`, `list`, and `uninstall`.

- **`--dry-run`**

  Reports the planned changes without copying or deleting skills or writing the manifest. It doesn't prevent restore; use `--no-restore` for that. Available for `install` and `uninstall`.

- **`--global-packages <PATH>`**

  The NuGet global packages folder to read extracted packages from. The folder must exist. This option overrides the `NUGET_PACKAGES` environment variable and the folder configured for NuGet, but it doesn't change where restore extracts packages. A relative path is resolved from the target's directory for target-based runs, and from the current directory when packages are named with `--package`. Available for `install` and `list`.

- **`-?|-h|--help`**

  Prints out a description of how to use the command.

- **`-i|--interactive`**

  Opens a paged checklist for choosing skills. For `install`, only skills that aren't installed are listed, nothing starts checked, and accepting adds the checked skills without refreshing or removing anything. For `uninstall`, only tracked skills are listed (with `--stale`, only stale skills), nothing starts checked, and the checked skills are removed. Requires a terminal when there are skills to choose from. Can be combined with `--dry-run`, `--package`, and `--stale`. For more information, see [Choose skills interactively](#choose-skills-interactively). Available for `install` and `uninstall`.

- **`--no-restore`**

  Doesn't restore the target. If the target hasn't been restored, the command fails instead of restoring it. The option is passed to `dotnet list package`, so the .NET SDK used for the target must support it. It has no effect when packages are named with `--package`. Available for `install` and `list`, and for `uninstall` together with `--stale`.

- **`-p|--package <ID@VERSION>`**

  For `install` and `list`, uses the specified package instead of a target. Specify an exact version, such as `Mockly@1.10.0`; floating versions and version ranges aren't accepted. Repeat the option to name several packages, with one version per package. Equivalent coordinates are deduplicated. The package must already be extracted in the NuGet global packages folder. Can't be combined with `--target`.

- **`-p|--package <ID[@VERSION]>`**

  For `uninstall`, removes only the skills installed from the specified package. `<ID>` matches the package's tracked skills, and `<ID@VERSION>` matches them only if that version is the one installed. Package IDs match case-insensitively, and versions are compared in normalized form. Specify the option at most once; a blank value is an error. Can't be combined with `--stale`.

- **`--stale`**

  For `uninstall`, removes only stale skills: tracked skills whose package the target no longer references, or references at a different version. Requires a solution or project. Can be combined with `--target`, `--no-restore`, `--dry-run`, and `--interactive`, but not with `--package`. For more information, see [Remove stale skills](#remove-stale-skills).

- **`-t|--target <PATH>`**

  The solution (`.slnx` or `.sln`), project (`.csproj`, `.fsproj`, or `.vbproj`), or directory to inspect. If you specify a directory, or omit the option, the command searches for a target as described in [Discover skills](#discover-skills). Can't be combined with `--package`. Available for `install` and `list`, and for `uninstall` together with `--stale`.

- **`--version`**

  Displays the tool version, which starts with `0.1.0` and can include build metadata after `+`. Available without a command.

## Examples

- List the skills available from the solution or project in the current directory:

  ```dotnetcli
  dotnet package-skills list
  ```

- List the skills available from a specific project:

  ```dotnetcli
  dotnet package-skills list --target App.Web/App.Web.csproj
  ```

- List the skills shipped in an exact package version:

  ```dotnetcli
  dotnet package-skills list --package Contoso.Widgets@2.3.0
  ```

- Install every discovered skill:

  ```dotnetcli
  dotnet package-skills install
  ```

- Preview an installation without restoring the target or changing any skills:

  ```dotnetcli
  dotnet package-skills install --dry-run --no-restore
  ```

- Choose which skills to install:

  ```dotnetcli
  dotnet package-skills install --interactive
  ```

- Try the picker without restoring the target or changing any skills:

  ```dotnetcli
  dotnet package-skills install -i --dry-run --no-restore
  ```

- Install skills from exact package versions into a different skills folder:

  ```dotnetcli
  dotnet package-skills install --package Contoso.Widgets@2.3.0 --package Mockly@1.10.0 --destination .claude/skills
  ```

- Remove every skill that the tool installed in the default destination:

  ```dotnetcli
  dotnet package-skills uninstall
  ```

- Remove the skills installed from one package version:

  ```dotnetcli
  dotnet package-skills uninstall --package Mockly@1.10.0
  ```

- Choose which installed skills to remove:

  ```dotnetcli
  dotnet package-skills uninstall --interactive
  ```

- Preview removing skills from a different skills folder:

  ```dotnetcli
  dotnet package-skills uninstall --destination .claude/skills --dry-run
  ```

- Preview removing the skills that no longer match the solution or project in the current directory:

  ```dotnetcli
  dotnet package-skills uninstall --stale --dry-run
  ```

- Remove the stale skills of a specific solution, choosing which ones:

  ```dotnetcli
  dotnet package-skills uninstall --stale --target src/MyApp.slnx --interactive
  ```

- Keep a committed skills folder in step with the project, for example in a CI job:

  ```dotnetcli
  dotnet package-skills install
  dotnet package-skills uninstall --stale
  ```

## See also

- [Functional specification](functional-spec.md)
- [Scenarios and expected behavior](scenarios.md)
- [README](../README.md)
