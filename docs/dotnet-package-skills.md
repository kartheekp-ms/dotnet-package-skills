---
title: dotnet-package-skills command
description: The 'dotnet-package-skills' command copies agent skills bundled in NuGet packages into a repository, where coding agents can find them.
ms.date: 09/24/2026
---
# dotnet-package-skills

**This article applies to:** ✔️ `dotnet-package-skills` version 0.1.0

## Name

`dotnet-package-skills` - Discovers, installs, and removes agent skills bundled in NuGet packages.

> [!NOTE]
> `dotnet-package-skills` is a .NET tool, not part of the .NET SDK. Install it with [`dotnet tool install`](https://learn.microsoft.com/dotnet/core/tools/dotnet-tool-install), for example `dotnet tool install --global dotnet-package-skills`. To install it from a local package feed, add `--add-source <FEED>`. The tool requires the .NET 8 runtime or later, and commands that read a solution or project also require the .NET SDK.

## Synopsis

```dotnetcli
dotnet-package-skills install [-d|--destination <PATH>] [--dry-run]
    [--global-packages <PATH>] [-i|--interactive]
    [-p|--package <ID@VERSION>...] [-t|--target <PATH>]

dotnet-package-skills list [-d|--destination <PATH>] [--global-packages <PATH>]
    [-p|--package <ID@VERSION>...] [-t|--target <PATH>]

dotnet-package-skills uninstall [-d|--destination <PATH>] [--dry-run]
    [-i|--interactive] [-p|--package <ID[@VERSION]>]

dotnet-package-skills uninstall --stale [-d|--destination <PATH>] [--dry-run]
    [-i|--interactive] [-t|--target <PATH>]

dotnet-package-skills [install|list|uninstall] -h|--help

dotnet-package-skills --version
```

## Description

Some NuGet packages include *agent skills*: instructions from the package author that teach coding agents how to use the package. Each skill is a folder that contains a `SKILL.md` file and any supporting files. Restore extracts these folders into the NuGet global packages folder, outside your repository, where agents don't look for them. The `dotnet-package-skills` command copies them into your repository.

To install the skills that your packages ship, run these commands from the root of your repository:

```dotnetcli
dotnet restore
dotnet-package-skills install
```

The skills are copied to `.agents/skills` under the current directory. If your agent reads skills from another folder, add `--destination`, for example `--destination .claude/skills`. If your solution or project isn't in the current directory, pass its path to both commands, for example `dotnet restore src/MyApp.slnx` and `dotnet-package-skills install --target src/MyApp.slnx`. To choose which skills to install, add `--interactive`. Run `install` again after you add or upgrade packages.

> [!IMPORTANT]
> Skills are instructions that your coding agent follows. Review them before you rely on them.

The command reads only the direct package references of your solution or project, not the packages that they depend on. It doesn't download packages or change your project files.

This article uses these terms:

- **Target**: the solution or project whose package references the command reads. Without `--target`, the command looks for one in the current directory, and then in its subdirectories. Reports show the target after `Target:`.
- **Skills folder**: the folder that skills are copied to, `.agents/skills` under the current directory unless you specify `--destination`. `--target` doesn't change it. Reports show it after `Destination:`.
- **Tracked skill**: a skill that the command installed, as recorded in the [manifest](#manifest-file) in the skills folder. The command updates and removes only tracked skills, never folders that you created.
- **Stale skill**: a tracked skill whose package the target no longer references at the version that the skill came from.

### List available skills

`dotnet-package-skills list` shows the skills that the target's packages ship, without copying anything. It runs [`dotnet list package`](https://learn.microsoft.com/dotnet/core/tools/dotnet-package-list) on the target to find its top-level (direct) package references, and then looks for skills in those packages in the NuGet global packages folder:

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

`NuGet cache` is the NuGet global packages folder that skills are read from, and `Destination` is the skills folder that `install` would copy them to. `list` doesn't look in the skills folder, so it doesn't show which skills are installed; the [manifest](#manifest-file) records those.

Restore the target before you run the command. The command doesn't run `dotnet restore` itself, although with the .NET 10 SDK, `dotnet list package` restores the target when it needs to, even during a dry run. If `dotnet list package` fails, the command shows what it reported and changes nothing; fix the problem, for example by restoring the target, and then run the command again. If a package that the target references isn't in the NuGet global packages folder, `list` skips the package and `install` stops.

To read skills from specific packages instead of a target, specify `--package <ID>@<VERSION>`. The package must already be in the NuGet global packages folder, because the command doesn't download it. If the package isn't there, the command finds no skills in it and doesn't warn you.

### Install and update skills

`dotnet-package-skills install` copies the target's skills into the skills folder and records them in the manifest. Each skill keeps its folder name from the package. After the same first lines as the `list` report, the report lists the skills that were copied:

```output
Copied 4 skills:
  contoso.widgets-widget-testing (Contoso.Widgets 2.3.0)
  contoso.widgets-widget-usage (Contoso.Widgets 2.3.0)
  fabrikam.testing-fakes (Fabrikam.Testing 1.4.0)
  fabrikam.testing-fixtures (Fabrikam.Testing 1.4.0)

These skills are instructions written by the package authors, and your coding agent will follow them. Review them before relying on them.
```

Run `install` again whenever your packages change; there's no separate update command. Each run compares the tracked skills with the target's packages:

| If you've... | `install`... |
| --- | --- |
| Added a package that ships skills | Copies its skills. |
| Upgraded or downgraded a package | Replaces its skills with those of the new version, and removes the ones that the new version no longer ships. |
| Removed a package | Keeps its skills, and lists them as in the following example. To remove them, see [Remove stale skills](#remove-stale-skills). |
| Changed nothing | Copies each package's skills again, replacing the installed copies. |

```output
2 installed skills belong to a package that the target no longer references:
  fabrikam.testing-fakes (fabrikam.testing 1.4.0)
  fabrikam.testing-fixtures (fabrikam.testing 1.4.0)
Run 'dotnet-package-skills uninstall --stale' to remove them.
```

> [!WARNING]
> `install` replaces the whole folder of each skill that it copies, including your edits and any files that you added. Keep your own instructions in separate skill folders; the command never changes folders that it didn't install.

With `--package`, `install` does the same for the packages that you name, and leaves all other skills alone. With `--interactive`, it only adds the skills that you choose; see [Choose skills interactively](#choose-skills-interactively). To preview an installation, add `--dry-run`. The report then lists the planned changes under `Would copy` and `Would remove`, and nothing changes.

The skills folder holds the skills of only one version of each package. If the target references a package at more than one version, or `--package` names more than one, `install` stops and names the versions. [Central Package Management](https://learn.microsoft.com/nuget/consume-packages/central-package-management) keeps all the projects in a repository on one version of each package.

Skill names are compared without regard to case. If two packages ship a skill with the same name, only one of them is copied. `install` also never replaces a tracked skill with another package's skill, or overwrites a folder that it didn't install. It skips such skills, and lists them in the report under `Warning:`. To give a skill name to another package, first remove the tracked skill, for example with `uninstall --package <ID>`.

### Remove skills

`dotnet-package-skills uninstall` removes every tracked skill from the skills folder. It doesn't remove folders that you created or any NuGet packages, and it doesn't need a target. If you installed skills into another folder, specify the same `--destination`.

```output
Destination: C:\src\MyApp\.agents\skills

Removed 2 skills:
  contoso.widgets-widget-testing (contoso.widgets 2.3.0)
  contoso.widgets-widget-usage (contoso.widgets 2.3.0)
```

To remove only one package's skills, add `--package <ID>`, or add `--package <ID>@<VERSION>` to remove them only if that version is installed. Add `--dry-run` to preview the removal, or `--interactive` to choose the skills to remove. If no skills are tracked, the command says so and succeeds.

### Remove stale skills

`install` keeps the skills of packages that the target no longer references. To remove them, run:

```dotnetcli
dotnet-package-skills uninstall --stale
```

This command removes every stale skill. Run `install` first, so that the skills of upgraded packages are updated instead of removed. The report looks like the `uninstall` report, with a `Target:` line first. Add `--dry-run` to preview the removal, or `--interactive` to choose among the stale skills.

`--stale` needs a target, which the command finds the same way as for `install`, or which you specify with `--target`.

Skills that you installed with `install --package` are stale unless the target references the same package version. For each skills folder, use either a target or `--package`, not both.

### Choose skills interactively

Add `--interactive` (`-i`) to choose skills in a paged checklist. `install -i` lists the skills that aren't installed yet, and `uninstall -i` lists the tracked skills, or with `--stale`, only the stale ones. Nothing starts checked. The following example shows an install checklist after one skill is checked:

```output
Which skills should be installed? (MyApp.slnx)
Installed skills aren't listed.

> [X] contoso.widgets-widget-usage - Correct usage patterns for the Contoso.Widgets library, including lifetime rules and the batching API. Use whenever code creates, configures, or disposes a Widget.
  [ ] fabrikam.testing-fakes - Create fakes and verify their calls in unit tests.
  [ ] fabrikam.testing-fixtures - Share expensive setup across tests with fixtures.

1 of 3 selected
(Press <space> to select, <enter> to accept)
(Press <up>/<down> to move, <Home>/<End> for first/last)
(Press <a> to select all, <c> to clear all, <Esc>/<q>/<Ctrl+C> to cancel)
Blue X: selected
```

Each row shows a skill's name and the `description` from its `SKILL.md` file. `>` marks the focused row, and `X` marks a checked skill. When the terminal shows color, the focused row and each `X` are blue.

| Key | Action |
| --- | --- |
| Up, Down | Move to the previous or next skill. |
| Left, Right, PageUp, PageDown | Move to the previous or next page. |
| Home, End | Go to the first or last skill. |
| Space | Check or uncheck the focused skill. |
| A, C | Check or clear all skills, on every page. |
| Ctrl+Up, Ctrl+Down | Scroll a description that's too long for the page. |
| Enter | Install or remove the checked skills. With `--dry-run`, only report what would change. |
| Esc, Q, Ctrl+C | Cancel without changing any skills. |

`install -i` only adds the skills that you check; it never updates or removes skills. Because of that, it first checks that the tracked skills match the packages, and stops before the checklist opens if they don't:

- With a target, it stops if any tracked skill is stale. Run `uninstall --stale`, and then try again.
- With `--package`, it stops if a named package is installed at another version. Run `uninstall --package <ID>` first, or run `install --package` without `--interactive` to switch versions.

Skills that `install` would skip because their name is in use aren't listed; the report after the checklist names them. If every skill is already installed, the checklist doesn't open, and the command reports `Nothing new to install.`

### Manifest file

The manifest, `.dotnet-package-skills.json` in the skills folder, records the skills that the command installed and the package version that each came from. Its shape follows the .NET local tool manifest, `dotnet-tools.json`:

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
| `version` | The manifest format version, `1`. |
| `packages` | One entry per package, keyed by the lowercase package ID. |
| `packages.<id>.version` | The package version that the skills were installed from. |
| `packages.<id>.skills` | The names of the skill folders installed from the package. |

If you commit the skills folder to source control, commit the manifest with it. The command writes the file the same way on every platform, in UTF-8 with LF line endings and a stable order, so it doesn't cause line-ending churn. Don't edit the file by hand: the command relies on it to decide which folders it can replace or remove, and drops properties that it doesn't recognize when it rewrites the file. When the last tracked skill is removed, the command deletes the manifest, and the skills folder if it's empty.

### Exit codes and troubleshooting

The command returns `0` on success, including when there's nothing to do or you cancel a checklist, and `1` on failure. Errors are written to standard error. Skipped skills are reported as warnings and don't change the exit code, so check the report when it matters that every skill was installed. Reports are meant for people: scripts should rely on the exit code, and read the manifest to find the installed skills.

Each of these errors stops the command before it changes any skills or the manifest:

| Problem | What to do |
| --- | --- |
| `dotnet list package` fails, for example because the target isn't restored. | Fix what it reports, for example by running `dotnet restore`, and then run the command again. |
| `install` reports packages that are missing from the NuGet global packages folder. | Restore the target into that folder, and then run `install` again. If you use `--global-packages`, restore into the same folder, for example with `dotnet restore --packages <PATH>`. |
| The target references a package at more than one version, or `--package` names more than one. | Align the versions, for example with [Central Package Management](https://learn.microsoft.com/nuget/consume-packages/central-package-management). With `--package`, name one version of each package. |
| `install --interactive` says that installed skills don't match the target, or that a named package is installed at another version. | Run the `uninstall` command that the error suggests, and then try again. To switch a package to another version instead, run `install` without `--interactive`. |
| The manifest can't be read. | Resolve any merge conflict in `.dotnet-package-skills.json`, or restore the file from source control. If the error says that the manifest uses a newer format version, update the tool. If it says that a pre-release version of the tool wrote the manifest, move the skills folder aside, and then run `install` again. |
| Another run of the command is using the skills folder. | The command waits up to 30 seconds for the other run to finish, and then stops. Run the command again after the other run finishes. |
| The terminal is too small for the checklist. | Enlarge the window, or run the command without `--interactive`. |

For other errors, run the command that the error suggests. Suggested commands repeat the `--target` and `--destination` that you specified, so you can run them as printed.

If a file system error interrupts a copy or removal, the command reports it but doesn't undo the changes that it already made, and copied folders might be missing from the manifest. Move the affected skill folders aside, and then run the command again.

## Commands

- **`install`**

  Copies the skills of the target's direct package references, or of the packages named with `--package`, into the skills folder, and updates the skills that it installed before.

- **`list`**

  Lists the skills available from the target's direct package references, or from the packages named with `--package`, without copying anything.

- **`uninstall`**

  Removes tracked skills from the skills folder. With `--stale`, removes only stale skills.

## Options

- **`-d|--destination <PATH>`**

  For `install`, `list`, and `uninstall`, specifies the skills folder. Defaults to `.agents/skills`. A relative path is resolved from the current directory, even when `--target` points somewhere else. `list` only shows the folder in its report. To remove skills, specify the folder that they were installed to.

- **`--dry-run`**

  For `install` and `uninstall`, reports the planned changes without copying or removing skills or writing the manifest. With the .NET 10 SDK, `dotnet list package` can still restore the target.

- **`--global-packages <PATH>`**

  For `install` and `list`, specifies the NuGet global packages folder to read packages from. The folder must exist. This option overrides the `NUGET_PACKAGES` environment variable and the folder configured for NuGet, but it doesn't change where restore extracts packages. Unlike `--destination`, a relative path is resolved from the target's directory, or from the current directory when you use `--package`.

- **`-?|-h|--help`**

  Prints out a description of how to use the command.

- **`-i|--interactive`**

  For `install` and `uninstall`, opens a checklist for choosing the skills to install or remove. `install` lists only skills that aren't installed, and only adds skills. Requires a terminal when there's something to choose. Can be combined with `--dry-run`, and with either `--package` or, for `uninstall`, `--stale`. For more information, see [Choose skills interactively](#choose-skills-interactively).

- **`-p|--package <ID@VERSION>`**

  For `install` and `list`, reads skills from the specified package instead of a target. Specify an exact version, such as `Contoso.Widgets@2.3.0`; floating versions and version ranges aren't accepted. Repeat the option to name several packages. Duplicates, such as `Mockly@1.10` and `Mockly@1.10.0`, count once. `install` accepts only one version of each package, while `list` shows every version that you name. The package must already be in the NuGet global packages folder; if it isn't, the command finds no skills in it and doesn't warn you. Can't be combined with `--target`.

- **`-p|--package <ID[@VERSION]>`**

  For `uninstall`, removes only the skills of the specified package. With a version, removes them only if that version is installed. Package IDs are compared without regard to case, and `1.10` matches `1.10.0`. Specify the option at most once; a blank value is an error. Can't be combined with `--stale`.

- **`--stale`**

  For `uninstall`, removes only stale skills. Requires a target. Can be combined with `--target`, `--dry-run`, and `--interactive`, but not with `--package`. For more information, see [Remove stale skills](#remove-stale-skills).

- **`-t|--target <PATH>`**

  For `install`, `list`, and `uninstall --stale`, specifies the solution (`.slnx` or `.sln`), project (`.csproj`, `.fsproj`, or `.vbproj`), or directory to read package references from. For a directory, or when the option is omitted, the command looks for a solution or project in that directory, and then in its subdirectories. Doesn't change the skills folder. Can't be combined with `--package`.

- **`--version`**

  Displays the tool version. Available without a command.

## Examples

- List the available skills:

  ```dotnetcli
  dotnet-package-skills list
  ```

- Install every available skill:

  ```dotnetcli
  dotnet-package-skills install
  ```

- Install the skills of a solution in a subfolder:

  ```dotnetcli
  dotnet-package-skills install --target src/MyApp.slnx
  ```

- Install skills into `.claude/skills` instead of `.agents/skills`:

  ```dotnetcli
  dotnet-package-skills install --destination .claude/skills
  ```

- Preview an installation without changing any skills:

  ```dotnetcli
  dotnet-package-skills install --dry-run
  ```

- Choose which skills to install:

  ```dotnetcli
  dotnet-package-skills install --interactive
  ```

- Install the skills of exact package versions, without a solution or project:

  ```dotnetcli
  dotnet-package-skills install --package Contoso.Widgets@2.3.0 --package Mockly@1.10.0
  ```

- Remove every tracked skill from `.agents/skills`:

  ```dotnetcli
  dotnet-package-skills uninstall
  ```

- Remove the skills of one package:

  ```dotnetcli
  dotnet-package-skills uninstall --package Contoso.Widgets
  ```

- Choose which skills to remove:

  ```dotnetcli
  dotnet-package-skills uninstall --interactive
  ```

- Preview the removal of stale skills:

  ```dotnetcli
  dotnet-package-skills uninstall --stale --dry-run
  ```

- Keep a committed skills folder in step with the solution or project, for example in a CI job. `install` runs first, so that the skills of upgraded packages are updated instead of removed:

  ```dotnetcli
  dotnet-package-skills install
  dotnet-package-skills uninstall --stale
  ```

## See also

- [Functional specification](functional-spec.md)
- [Scenarios and expected behavior](scenarios.md)
- [README](../README.md)
