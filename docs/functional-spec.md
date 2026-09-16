# .NET Package Skills: Functional Specification

## 1. Purpose and scope

The tool makes agent skills shipped in NuGet packages available inside a developer's repository, where their coding agent can find them. It supports three jobs: discover available skills, install the skills the developer wants, and remove previously installed skills.

It copies whole skill folders, including supporting documents, from the NuGet cache. It does not move or modify the source package, change project package references, install an agent, or configure an MCP server. In this version, only direct package dependencies are scanned.

The default destination is `.agents\skills` under the directory where the command runs. Developers can choose another destination, such as `.claude\skills`. Agent support for a destination remains the agent's responsibility.

Examples below use illustrative packages and paths. Interactive page sizes and line wrapping vary with the terminal dimensions; the examples are not fixed screen layouts.

## 2. Command structure

Both invocation forms are equivalent:

```powershell
dotnet package-skills <command> [options]
dotnet-package-skills <command> [options]
```

There are three subcommands. Interactive selection, previews, and JSON are options, not additional subcommands.

| Command | Customer intent | Effect on destination skills |
| --- | --- | --- |
| `list` | See which package-provided skills are available. | No changes. |
| `install` | Copy or refresh available skills. | Noninteractive target runs can prune stale skills; interactive runs remove only explicitly unchecked skills. |
| `uninstall` | Remove skills previously installed by this tool. | Removes all matching tracked skills, or an interactive selection. |
| `--help` | Learn the commands and options. | No changes. |
| `--version` | Identify the tool build. | No changes. |

The tool requires a compatible .NET runtime; current builds target .NET 8 and .NET 10. Project discovery also uses the installed .NET SDK.

Installing the .NET tool itself is separate from installing skills. For evaluation with a supplied local tool package:

```powershell
dotnet tool install --global --add-source C:\tool-feed dotnet-package-skills --version 0.1.0
```

### Help and version

```powershell
dotnet package-skills --help
dotnet package-skills install --help
dotnet package-skills list --help
dotnet package-skills uninstall --help
dotnet package-skills --version
```

Representative root help:

```text
Usage:
  dotnet-package-skills [command] [options]

Options:
  -?, -h, --help  Show help and usage information
  --version      Show version information

Commands:
  install    Copy skills bundled in NuGet packages into the repository.
  list       Show which packages ship skills, without copying anything.
  uninstall  Remove skills this tool previously copied in.
```

Version output starts with `0.1.0` and may include build metadata after `+`.

## 3. Discover available skills: `list`

```powershell
dotnet package-skills list
```

The tool finds a solution or project, reads its resolved direct dependencies, and looks for immediate subfolders of each package's `skills` directory that contain `SKILL.md`. Without `--target`, it prefers a top-level solution, then a top-level project, then a nested solution, then a nested project. `.slnx` precedes `.sln`. Multiple matches are resolved deterministically, not through an ambiguity prompt; the chosen path appears in `Target:`. Name a target when that choice matters.

Sample output:

```text
Target:      C:\work\Demo\App.slnx
NuGet cache: C:\packages
Destination: C:\work\Demo\.agents\skills
Scanned 2 packages (direct).

Found 3 skills:
  contoso.widgets-testing (Contoso.Widgets 2.3.0)
  contoso.widgets-usage (Contoso.Widgets 2.3.0)
  mockly-usage (Mockly 1.10.0)
```

`list` shows what is available from packages, not an inventory of installed skills. It never copies skills or writes the ownership manifest. It can trigger project restore when necessary; add `--no-restore` to prohibit that.

Other ways to scope discovery:

```powershell
dotnet package-skills list --target C:\work\Demo\App.slnx
dotnet package-skills list --target C:\work\Demo\App.Web\App.Web.csproj
dotnet package-skills list --package Contoso.Widgets@2.3.0
```

An explicit package must already be extracted in the selected NuGet cache. Naming it does not download it or add it to a project. Such reports use `Target: (packages named on the command line)` and `Scanned N packages (named explicitly)`.

The cache directory itself must already exist before automatic project restore can be attempted. If it is absent, restore the project first. When projects resolve different versions of one package, each package/version pair is counted separately; colliding names are resolved deterministically, not by choosing the newest semantic version. `list` does not read destination ownership, so its first discovery candidate can differ from the owner-preferred candidate used by `install`.

## 4. Install or refresh skills: `install`

```powershell
dotnet package-skills install
```

Without `--interactive`, the tool attempts to install every discovered skill. It preserves the authored folder names and records ownership in `.dotnet-package-skills.json` inside the destination. Refreshing a tracked skill replaces its whole folder, including local edits or added files. Protection for hand-written skills applies to separate, untracked folders.

The report uses the same context header as `list`. Its result section looks like:

```text
Copied 3 skills:
  contoso.widgets-testing (Contoso.Widgets 2.3.0)
  contoso.widgets-usage (Contoso.Widgets 2.3.0)
  mockly-usage (Mockly 1.10.0)

These skills are instructions written by the package authors, and your coding agent will follow them. Review them before relying on them.
```

### Refresh and cleanup rules

| Scope | Behavior |
| --- | --- |
| Noninteractive, complete target discovery | Refresh current skills and remove stale tracked skills. This cleanup is called pruning. Paths protected by an ownership conflict are retained. |
| `--package` | Install additively; do not infer that unrelated installed packages should be removed. |
| Interactive install, including `--target` | Show current candidates and, for a target, retained installed copies. Installed copies start checked. Remove only what the user explicitly unchecks; never apply undisplayed automatic pruning. |
| A different package claims an installed name | Preserve the existing owner and skip the conflicting copy with a warning in every mode. If the target also supplies the current owner's candidate, refresh that candidate rather than letting the conflict block it. Replacement requires an explicit uninstall first. |

If any resolved package is missing from a target's cache, `install` stops before any skill or manifest change, including with `-i` or `--dry-run`. `list` remains available and identifies the missing packages. Restore into the selected cache before retrying.

There is no separate update command: running `install` again refreshes the applicable skill copies.

```powershell
dotnet package-skills install --package Contoso.Widgets@2.3.0 --package Mockly@1.10.0
dotnet package-skills install --destination .claude\skills
```

### Preview without installing

```powershell
dotnet package-skills install --dry-run --no-restore
```

Result excerpt:

```text
Would copy 3 skills:
  contoso.widgets-testing (Contoso.Widgets 2.3.0)
  contoso.widgets-usage (Contoso.Widgets 2.3.0)
  mockly-usage (Mockly 1.10.0)
```

Any planned removals appear under `Would remove`. A dry run does not copy or delete skills or create/update the ownership manifest. It does not, by itself, prohibit project restore; that is what `--no-restore` controls.

## 5. Choose skills interactively

```powershell
dotnet package-skills install --interactive
dotnet package-skills install --package Contoso.Widgets@2.3.0 -i
dotnet package-skills install -i --dry-run --no-restore
```

Representative page after making a selection:

```text
Which skills should be installed? (App.slnx)  page 1 of 3

  [ ] contoso.widgets-testing - Test widget behavior in isolation.
> [x] contoso.widgets-usage - Create and configure widgets safely.
      Use when managing widget lifetimes, retries, and batching.
  [x] mockly-usage - Create test doubles and verify interactions.

2 of 12 selected; 1 to install; 1 to remove
(Press <space> to select, <enter> to accept)
(Press <up>/<down> to move, <Home>/<End> for first/last)
(Press <left>/<right>, <PageUp>/<PageDown> to change page)
(Press <a> to select all, <c> to clear all, <Esc>/<q>/<Ctrl+C> to cancel)
Green: install Red: remove
```

In this example, `contoso.widgets-usage` is new, `mockly-usage` is already installed, and the previously installed testing skill has been unchecked.

### Selection and presentation

- Installed skills start checked; new skills start unchecked. Target-based pickers also include installed skills the target no longer supplies, labeled as retained installed copies.
- Acceptance installs or refreshes eligible checked candidates, retains checked installed copies, and removes only explicitly unchecked installed skills. Every planned removal has a visible row and is included in the removal count.
- Even when complete discovery finds zero candidates, existing tracked skills are offered and kept checked by default. There is no hidden cleanup path behind an empty picker.
- Conflicting candidates are excluded from installation choices and reported as skipped. A row from a different package cannot select, deselect, or remove the current owner's same-named skill.
- The format is always `skill-name - description`, without a package/version suffix or a separate description column. Authored package prefixes in skill names remain intact. Long display names may be clipped with `...`; their canonical identities are unchanged.
- Descriptions wrap beneath the skill text with a small list indent, using the available width. Page sizes reflect rendered lines, not a fixed number of skills. A list that fits needs no paging.
- Live resizing recalculates wrapping and pagination while preserving focus and selection. Oversized descriptions can be scrolled while their skill row remains visible.

| Visual cue | Meaning |
| --- | --- |
| Blue `>` | Keyboard focus; independent of the pending action. |
| Green skill name/checkbox | Will be installed. |
| Red skill name/checkbox | Will be removed. |
| Normal text | No pending addition/removal. Descriptions remain neutral. |
| `+` / `-` when color is disabled | Pending installation/removal. `NO_COLOR` is honored. |

| Key | Behavior |
| --- | --- |
| Up / Down | Move between skills; wrap at the beginning/end. |
| Left / Right or PageUp / PageDown | Move between pages. |
| Home / End | Go to the first/last skill. |
| Space | Toggle the focused skill. |
| A / C | Select all / clear all, across all pages. |
| Ctrl+Up / Ctrl+Down | Scroll an oversized description. |
| Enter | Accept. With `--dry-run`, report only. |
| Esc / Q / Ctrl+C | Cancel without changing destination skills. |

Every keyboard-help line begins with `Press`. Paging/scrolling hints appear only when applicable. An unusably small terminal, at startup or after a resize, fails with exit code `1` and guidance to enlarge it. Destination skills are unchanged, but the in-memory selection must be made again. If ownership changes while a picker is open, acceptance also fails without applying the stale choice.

Descriptions are read from YAML frontmatter in `SKILL.md`. Missing descriptions show `No description provided.` Invalid or unreadable descriptive metadata produces a visible warning, but does not prevent selecting the skill. Descriptions are not rewritten, executed, or added to ordinary reports or JSON.

Cancellation output:

```text
Cancelled. Nothing was copied or removed.
```

## 6. Remove installed skills: `uninstall`

```powershell
dotnet package-skills uninstall
```

This removes all skills tracked by the destination's ownership manifest. It does not remove NuGet packages or hand-written skills, and does not require a solution or the NuGet cache.

Sample output:

```text
Destination: C:\work\Demo\.agents\skills

Removed 2 skills:
  contoso.widgets-usage (Contoso.Widgets 2.3.0)
  mockly-usage (Mockly 1.10.0)
```

Common variants:

```powershell
dotnet package-skills uninstall --package Mockly
dotnet package-skills uninstall --package Mockly@1.10.0
dotnet package-skills uninstall --destination .claude\skills
dotnet package-skills uninstall --dry-run
dotnet package-skills uninstall --interactive
```

A bare package ID matches all its tracked versions; `ID@VERSION` narrows removal to one version. Both modes use the same case-insensitive package matching and normalized version comparison: for example, `1.10` matches `1.10.0`. An explicitly blank, whitespace-only, or missing filter value is an error, never an instruction to remove everything. Uninstall accepts the package option only once; repeated occurrences and aliases are rejected even if the final value is absent. Only omitting `--package` means no filter. A dry run reports `Would remove` and preserves the files.

The interactive picker lists only manifest-owned skills and reads descriptions from their installed copies. Nothing starts checked. A check means removal, not retention:

```text
Which skills should be uninstalled?

> [x] contoso.widgets-usage - Create and configure widgets safely.
  [ ] mockly-usage - Create test doubles and verify interactions.

1 of 2 selected; 1 to remove
(Press <space> to select, <enter> to accept)
(Press <up>/<down> to move, <Home>/<End> for first/last)
(Press <a> to select all, <c> to clear all, <Esc>/<q>/<Ctrl+C> to cancel)
Red: remove
```

Accepting removes only the checked skill. If ownership changes while the picker is open or while waiting for another operation, acceptance fails without applying that stale selection. A missing installed `SKILL.md` does not prevent removing its tracked folder. With no tracked skills, the command succeeds without opening a picker:

```text
Destination: C:\work\Demo\.agents\skills

Nothing to remove. No skills installed by this tool were found there.
```

## 7. Complete option reference

| Option | Applies to | Contract |
| --- | --- | --- |
| `-t, --target <PATH>` | `list`, `install` | Solution, project, or directory to search. Supported files: `.slnx`, `.sln`, `.csproj`, `.fsproj`, `.vbproj`. Defaults to discovery from the current directory. |
| `-p, --package <ID@VERSION>` | `list`, `install` | Exact package coordinates instead of a target. Repeatable; equivalent repeated coordinates are deduplicated. Floating versions and ranges are rejected. |
| `-p, --package <ID[@VERSION]>` | `uninstall` | One occurrence of a nonempty package filter, optionally restricted to a normalized version. Same matching in both modes. |
| `-d, --destination <PATH>` | All three | Skills destination; default `.agents\skills`. Uninstall must use the same destination used for installation. |
| `--global-packages <PATH>` | `list`, `install` | Existing extracted-package cache to use. Overrides `NUGET_PACKAGES` and the NuGet-configured cache. |
| `--no-restore` | `list`, `install` | Do not restore; fail when the target's resolved package list is unavailable. |
| `--dry-run` | `install`, `uninstall` | Report planned destination changes without applying them. |
| `-i, --interactive` | `install`, `uninstall` | Open the paginated picker. Installed copies start retained on install; only explicit choices remove skills. Requires a terminal when there are rows to choose. |
| `--json` | All three | Replace the human-readable success report with machine-readable JSON. Does not make the operation a preview. |
| `-?, -h, --help` | Root and all three | Display usage and supported options. |
| `--version` | Root | Display version information. |

**Combination rules:** `--target` cannot be combined with `--package`; `--interactive` cannot be combined with `--json`. Interactive selection can be combined with `--dry-run` and package filters. `list` has neither `--interactive` nor `--dry-run`.

**Path rule:** a relative destination is based on the invocation directory, not automatically on the directory containing `--target`. A relative `--global-packages` override is resolved from the target's directory in target mode and the invocation directory in named-package mode. That override selects the read cache; it does not reconfigure NuGet restore.

## 8. Machine-readable output: `--json`

Scripts and agents receive one JSON object on successful completion. Field names are consistently camelCase. Skill identities use `packageId`, `packageVersion`, and `skillName` in every command.

Example: `dotnet package-skills list --package Mockly@1.10.0 --json`

```json
{
  "globalPackagesFolder": "C:\\packages",
  "destination": "C:\\work\\Demo\\.agents\\skills",
  "packagesScanned": 1,
  "dryRun": true,
  "skills": [
    {
      "packageId": "Mockly",
      "packageVersion": "1.10.0",
      "skillName": "mockly-usage",
      "sourcePath": "C:\\packages\\mockly\\1.10.0\\skills\\mockly-usage",
      "relativePath": "mockly-usage"
    }
  ],
  "skillsDiscovered": 1,
  "removed": [],
  "skipped": [],
  "notOnDisk": []
}
```

`install --json` uses the same shape: `dryRun` is false unless requested, and `skills` contains accepted installations rather than the discovery list. `skillsDiscovered` counts unique discovered destination names before installation filtering, not every colliding candidate. Target-based results also include `target`; named-package results omit it. `list` always reports `dryRun: true`.

`removed` identifies removals; `skipped` contains skill objects with a `reason`. `notOnDisk` is an array of strings such as `"Mockly 1.10.0"`, not package objects. Empty results retain empty arrays. For example, a discovery report can contain these diagnostic fields:

```json
{
  "skipped": [
    {
      "packageId": "Beta",
      "packageVersion": "2.0.0",
      "skillName": "shared-skill",
      "relativePath": "shared-skill",
      "reason": "conflicts with Alpha 1.0.0 skill 'shared-skill', which was selected first"
    }
  ],
  "notOnDisk": ["Mockly 1.10.0"]
}
```

Example: `dotnet package-skills uninstall --package Mockly --json`

```json
{
  "destination": "C:\\work\\Demo\\.agents\\skills",
  "dryRun": false,
  "removed": [
    {
      "packageId": "Mockly",
      "packageVersion": "1.10.0",
      "skillName": "mockly-usage",
      "relativePath": "mockly-usage"
    }
  ]
}
```

**Scripts must check the exit code before parsing stdout.** Operational errors go to stderr and do not produce a JSON success object. Argument/usage errors can also print help on stdout.

## 9. Safety, empty results, and errors

| Situation | User-visible behavior |
| --- | --- |
| No package ships a discoverable skill | Successful report: `No bundled skills found. None of the scanned packages ship a skills/ folder.` |
| Skills exist but none are accepted | Report `Copied no skills.` or `Would copy no skills.`, with any skipped-item details. |
| A resolved package is absent from the cache | `list` reports it; a target-based `install` fails before changes. Explicit-package installs remain additive and report unavailable packages without pruning other skills. |
| A destination name conflicts with another skill, an untracked folder, or a different installed owner | Warn and skip the conflicting copy. Preserve the current owner, including during target-based cleanup. |
| A package filter is explicitly blank or missing its value | Fail; never broaden a selective uninstall to all tracked skills. |
| The ownership manifest is absent | Existing folders are not assumed to belong to the tool. |
| The ownership manifest is unreadable, malformed, or unsafe | Fail before modifying destination skills; preserve the manifest and explain how to repair/restore it. Missing ownership data, duplicate JSON properties, and duplicate case-insensitive skill claims are invalid. `list` remains available. |
| Invalid option combination, missing target, failed restore, or filesystem error | Report an actionable error and return a non-zero exit code. |

The manifest is written after successful installation, not by `list`, a cancelled picker, or a dry run. Removing the last tracked entry deletes the manifest. The destination folder is deleted only if it is empty; hand-written skills keep that folder alive. Removing a tracking entry is reported even when its skill folder had already been deleted.

Cooperating tool processes serialize reads and changes for a destination. Ownership is loaded and checked inside that critical section, which remains held through copying, removal, and manifest persistence. A busy destination produces retry guidance rather than overlapping mutations. This is local-process coordination, not a distributed filesystem transaction. Equivalent Windows path spellings share that coordination. If a destination alias changes while an operation waits for access, the operation fails before modifying the newly resolved location.

Successful operations, empty results, and cancellation return exit code `0`. Command failures return `1`. Warnings/skipped skills can still accompany exit code `0`; automation should inspect the report when completeness matters.

Skill metadata is not a security review of the instructions. Developers remain responsible for deciding what their agent should trust. Unexpected filesystem failures are reported, but transactional rollback of a partially completed copy/removal is not provided in this version. Such failures can leave copied folders absent from the manifest. Restore a verified backup or move affected folders aside before retrying; do not blindly delete untracked guidance.

## 10. Product review checklist

| Scenario | Expected customer outcome |
| --- | --- |
| Discover before deciding | `list` shows available skills without installing them. |
| Try the picker safely | `install -i --dry-run --no-restore` previews a selection without writing skills, a manifest, or restore artifacts. |
| Make an informed choice | Read descriptions, navigate pages, select skills, and accept. |
| Resize during selection | Text reflows and selections are retained while the window remains large enough; an unusable size fails without changing files. |
| Review stale installed skills | Target-based interactive install shows retained copies checked by default, even with zero new candidates. |
| Encounter incomplete discovery | Target-based install fails before copying or removing anything; `list` shows the missing packages. |
| Encounter another package's owned name | Preserve the installed owner and warn; replacement requires explicit removal first. |
| Remove selectively | `uninstall -i` offers tracked skills only and removes only checked items. |
| Use package filters in scripts | Blank filters fail, and normalized version matching is identical with and without `-i`. |
| Keep locally authored guidance | Installation conflicts and removal never take ownership of untracked skill folders. |
| Automate reliably | Use JSON plus exit-code handling; distinguish empty success, skipped items, and failure. |
| Recover from a damaged ownership record | Receive an explicit error; repair or restore the preserved manifest before retrying. |
