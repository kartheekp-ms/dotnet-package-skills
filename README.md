# dotnet-package-skills

Copies agent skills bundled inside NuGet packages into a folder your coding agent actually reads.

For a product-oriented command reference and sample outputs, see the
[functional specification](docs/functional-spec.md).

## The problem

Package authors are the domain experts on their own libraries, and some of them now ship an
**agent skill** inside the package — instructions covering the conventions, gotchas, and correct
usage patterns for that library. Those files are packed at
`skills/<package-id>-<skill-name>/SKILL.md`.

Restore extracts the package into the **NuGet global packages folder** (`~/.nuget/packages` by
default), which lives outside your repository and is shared by every project on the machine.
Coding agents only scan a skills directory *inside* the working repo. So the skill is on disk,
correct, and invisible.

This tool bridges that gap.

```
~/.nuget/packages/mockly/1.10.0/skills/mockly-usage/SKILL.md  ← where restore puts it
        ↓
.agents/skills/mockly-usage/SKILL.md                          ← where your agent looks
```

## Install

```bash
dotnet tool install --global dotnet-package-skills
```

## Use

From your repository root:

```bash
dotnet package-skills install
```

That is the whole workflow. It finds your solution or project, lists its packages, locates each
direct dependency in the NuGet cache, and copies any bundled skills into `.agents/skills/`.

Run it again after adding or upgrading packages. It is idempotent: it refreshes what is current
and removes what is not.

### Commands

| Command | What it does |
| --- | --- |
| `install` | Copy bundled skills into the destination. Noninteractive target runs clean up stale skills; interactive runs remove only skills you explicitly turn off. |
| `list` | Show which packages ship skills, without copying anything. |
| `uninstall` | Remove skills this tool copied in. Add `--interactive` to pick them. |

### What to point it at

Three ways to say which packages to take skills from:

```bash
dotnet package-skills install                              # auto-detect solution or project
dotnet package-skills install --target src/MyApp.slnx      # a specific solution or project
dotnet package-skills install --package Mockly@1.10.0      # exact packages, no project needed
```

`--package` is repeatable and needs an **exact version** — `Mockly@1.*` and `Mockly@[1.0,2.0)` are
refused. Resolving a range means picking a version, and the only correct answer to "which version"
comes from a project's restore, which is what `--target` is for. Guessing would copy skills
describing a release you do not actually reference.

`--target` and `--package` cannot be combined; both answer the same question.

Naming packages explicitly is **additive** — it copies what you asked for and leaves everything
else alone. Only a target describes a complete set of packages, so only a target prunes.
Automatic pruning applies only to noninteractive installs with complete discovery. If a resolved
target package is missing from the selected cache, installation fails before any destination
changes; restore first. `list` can still report what is missing.

### Choosing which skills to install

By default `install` copies everything it finds. Add `--interactive` to choose:

```bash
dotnet package-skills install --interactive                          # everything the project references
dotnet package-skills install --package Mockly@1.10.0 --interactive  # just one package's skills
```

It composes with `--target` and `--package`, so you can narrow to a single package first and then
pick among the skills it ships — which is what you want when one package bundles a dozen of them.

```
Which skills should be installed? (MyApp.slnx)  page 1 of 3

> [x] mockly-setup - Configure mocks and test
      doubles for unit tests.
  [ ] mockly-usage - Common Mockly usage patterns.

1 of 24 selected; 1 to install; 0 to remove
(Press <space> to select, <enter> to accept)
```

Each description follows the authored skill name immediately after ` - `, without a padded
column or a package/version suffix. Package prefixes in authored names are kept, and continuation
lines flow beneath the skill text, using the available width rather than leaving a name-sized gap.
The highlighted `>` is blue; skill names and checkboxes
are green for a pending installation, red for a pending removal, and their normal color when nothing changes. Descriptions
stay neutral so they are easy to read. The summary counts pending actions; there is no separate
status column. With `NO_COLOR` set, or on a terminal without color support, compact `+` and `-`
markers indicate installation and removal instead.

The keyboard hints appear below the list, using Aspire's
`(Press <space> to select, <enter> to accept)` style. Every keyboard-help line starts with `Press`,
including movement, paging, select-all, clear-all, cancel, and description scrolling. Controls
that do nothing are left out. These keys work:

| Key | Does |
| --- | --- |
| `up` / `down` | Move, wrapping around at either end |
| `left` / `right`, `pgup` / `pgdn` | Previous / next page |
| `home` / `end` | Jump to the first / last skill |
| `space` | Toggle the highlighted skill |
| `a` / `c` | Select all / clear all, across every page |
| `ctrl+up` / `ctrl+down` | Scroll a description when one skill is taller than a page |
| `enter` | Confirm the selection |
| `esc` / `q` / `ctrl+c` | Cancel, changing nothing |

Pages are measured in rendered lines, including wrapped descriptions and keyboard hints, rather
than a fixed number of skills. Each ordinary skill stays together on one page. A description too
long for a page can be scrolled without changing the selection. Resizing the terminal reflows the
page in place while preserving the highlighted skill and checked items, even during a redraw.
Old picker frames are not pushed into scrollback. When scrolling an oversized description, the
skill row stays visible while its continuation lines scroll. Short lists and partial final pages
do not leave a screenful of blank rows, and a single page has no page counter.

Descriptions come from the top-level YAML `description` in each package's `SKILL.md`. Missing
descriptions say `No description provided.`; unreadable or malformed metadata shows an explicit
description warning without hiding the skill or preventing its selection. Only interactive
pickers read this metadata: regular reports, JSON output, and the ownership manifest are unchanged.

Skills you already have start selected, and a new skill is always an explicit opt-in. Target-based
pickers also show installed copies that the project no longer supplies, kept checked by default.
Even with no new candidates, they remain available for review instead of being removed silently.
Interactive installation removes only explicit deselections; it never adds hidden pruning to the
removal count. Accepting can still refresh the checked skills that have current package sources.

**Turning off a skill you already have deletes it** — including under `--package`, where install is
otherwise additive. Pruning is inferred from a complete package set; deselecting is you saying so.

`--interactive` needs a terminal and cannot be combined with `--json`. Pair it with `--dry-run` to
see what a selection would change before committing to it.

### Choosing what to remove

`uninstall` takes `--interactive` too, and lists only what this tool installed — never a skill you
wrote yourself, because it reads the manifest rather than the folder:

```bash
dotnet package-skills uninstall --interactive
```

```
Which skills should be uninstalled?

> [x] mockly-setup - Configure mocks and test
      doubles for unit tests.
  [ ] mockly-usage - Common Mockly usage patterns.

1 of 2 to remove
(Press <space> to select, <enter> to accept)
```

Nothing starts ticked, so a mistaken enter removes nothing. Narrow the list first with
`--package` if you only care about one, and add `--dry-run` to see the outcome without it
happening. Descriptions are read from the installed copies, not from the NuGet cache. A missing
or damaged `SKILL.md` does not prevent removal of a manifest-owned skill.
Package matching ignores case, and version filters are normalized in both modes (`1.10` matches
`1.10.0`). Blank, missing, or repeated uninstall `--package` values are errors, not an unfiltered uninstall.

### Options

| Option | Applies to | Description |
| --- | --- | --- |
| `-t, --target <PATH>` | install, list | Solution or project to inspect. Defaults to searching the current directory. |
| `-p, --package <ID@VERSION>` | install, list | Take skills from an exact package instead of a project. Repeatable. No floating versions. |
| `-d, --destination <PATH>` | install, list | Where skills are copied. Default `.agents/skills`. |
| `-d, --destination <PATH>` | uninstall | Where to remove them from. Must match the one you installed to. |
| `--no-restore` | install, list | Fail instead of restoring when the target has not been restored. |
| `--global-packages <PATH>` | install, list | Override the NuGet global packages folder. |
| `-i, --interactive` | install | Choose which skills to install, with descriptions and pagination. Combines with `--target` or `--package`. Not with `--json`. |
| `-i, --interactive` | uninstall | Choose which installed skills to remove, with descriptions and pagination. Lists only what this tool installed. Not with `--json`. |
| `-p, --package <ID[@VERSION]>` | uninstall | Remove only skills from this package — every version, or one. |
| `--dry-run` | install, uninstall | Report what would change without writing anything. |
| `--json` | all | One JSON object on stdout instead of the report. See [Scripting it](#scripting-it). |

### Targeting another agent's folder

`.agents/skills` is the vendor-neutral default. Point `--destination` anywhere else:

```bash
dotnet package-skills install --destination .claude/skills
dotnet package-skills install --destination .codex/skills
```

`uninstall` takes the same option, and needs it: it only looks where you point it, so removing
what you put in `.claude/skills` means saying so again.

```bash
dotnet package-skills uninstall --destination .claude/skills
```

### Scripting it

`--json` replaces the report with a single object on stdout. It changes nothing else: the same
work happens and the same exit code comes back.

A skill is spelled the same way everywhere it appears, in every command, so one reader handles
all of them:

| Field | Meaning |
| --- | --- |
| `packageId` | Package id, in NuGet's casing. |
| `packageVersion` | Resolved version. |
| `skillName` | Skill folder name inside the package. |
| `relativePath` | Where it sits under the destination, with forward slashes. |
| `sourcePath` | Where it was read from. Absent once the skill is gone. |
| `reason` | Why it was passed over. Only on `skipped`. |

`install` and `list` return the same shape:

```json
{
  "target": "/repo/App.slnx",
  "globalPackagesFolder": "/home/you/.nuget/packages",
  "destination": "/repo/.agents/skills",
  "packagesScanned": 7,
  "dryRun": false,
  "skills": [
    {
      "packageId": "Contoso.Widgets",
      "packageVersion": "2.3.0",
      "skillName": "contoso.widgets-usage",
      "sourcePath": "/home/you/.nuget/packages/contoso.widgets/2.3.0/skills/contoso.widgets-usage",
      "relativePath": "contoso.widgets-usage"
    }
  ],
  "skillsDiscovered": 35,
  "removed": [],
  "skipped": [],
  "notOnDisk": []
}
```

`skillsDiscovered` counts what was found, while `skills` lists what was installed, so a script can
tell "no package ships a skill" apart from "you chose none of the ones that do". `target` is left
out entirely when you name packages with `--package`. `list` always reports `"dryRun": true`.

`uninstall` returns `destination`, `dryRun`, and `removed`.

Operational failures print to **stderr** and exit non-zero without a JSON success object.
Argument/usage errors can also print help on stdout. Check the exit code before parsing:

```bash
if json=$(dotnet package-skills list --json); then
  echo "$json" | jq -r '.skills[].skillName'
else
  echo "failed" >&2
fi
```

`--json` cannot be combined with `--interactive`: a script has nobody to answer the prompt.

## What you get

Each authored skill folder lands directly under the destination:

```
.agents/skills/
├── .dotnet-package-skills.json            # what this tool copied in; do not hand-edit
├── contoso.widgets-widget-usage/
│   ├── SKILL.md
│   └── references/
│       └── batching.md
└── contoso.widgets-widget-testing/
    └── SKILL.md
```

The tool preserves the skill folder name from the package. Package id and version remain in the
install manifest for attribution and uninstall filtering, but they are not added to the path.

The manifest groups the copied skill folder names by package and version:

```json
{
  "installed": [
    {
      "package": "Contoso.Widgets",
      "version": "2.3.0",
      "skills": [
        "contoso.widgets-widget-testing",
        "contoso.widgets-widget-usage"
      ]
    }
  ]
}
```

The manifest keeps its own wording — `package`, `version`, `skills` — rather than the
`packageId`/`packageVersion`/`skillName` that `--json` uses. It is a stored file, and renaming its
fields would strand every manifest already written to disk.

Package authors should prefix every folder with their lowercased package id, as shown above. This
keeps names globally unique when skills from many packages share one destination. The convention is
documented rather than enforced, so existing safe names still work.

### Name collisions

Destination names are compared case-insensitively. If two package skills choose the same name, the
first one in deterministic package order is copied and later collisions are skipped with a warning.
An existing destination folder not tracked by this tool is treated as user-owned and is also
skipped, never overwritten.
The same protection applies to a name already owned by a different package: every install mode
warns and preserves that owner rather than transferring it automatically. Explicitly uninstall
the old skill before installing its replacement. Upgrading the same package remains supported.
If both the owner and another package offer the same name, installation prefers the owner's
candidate so the conflict does not prevent a legitimate refresh.

Refreshing a tracked skill replaces its entire folder, including local edits and added files.
Keep hand-written guidance in separate, untracked skill folders.

### Package versions

The destination does not support side-by-side skill copies from multiple versions of one package.
Use [NuGet Central Package Management](https://learn.microsoft.com/nuget/consume-packages/central-package-management)
to keep projects in a repository on one package version. If several resolved versions provide the
same skill folder, the first is copied and the others are reported as collisions.

### Should I commit this folder?

Either is defensible. Commit it so the whole team and CI get the skills without running anything,
or gitignore it and let each machine refresh it. Pick one and say so in your contributing guide.

## For package authors: shipping a skill

Put each skill under `skills/<package-id>-<skill-name>/`, with its own `SKILL.md` and any supporting
files. Prefixing the folder with your lowercased package ID keeps your skills from colliding with
other packages on the consumer's machine.

```xml
<ItemGroup>
  <!-- %(RecursiveDir) is what preserves the folder structure. Without it every file
       collapses into one directory and the skill loses its reference documents. -->
  <None Include="skills/**/*"
        Pack="true"
        PackagePath="skills/%(RecursiveDir)%(Filename)%(Extension)" />
</ItemGroup>
```

A complete working example is in [`samples/Contoso.Widgets`](samples/Contoso.Widgets).

Every skill must have its own immediate subdirectory under `skills/`; a lone `skills/SKILL.md` is
not discovered.

Give each skill a useful `description` in its YAML frontmatter so customers can decide whether
they need it:

```yaml
---
name: contoso.widgets-widget-usage
description: >
  Correct usage patterns for Contoso.Widgets, including lifetime rules and batching.
  Use when creating, configuring, or disposing a Widget.
---
```

Plain, quoted, literal (`|`), and folded (`>`) descriptions are supported. The interactive picker
reads only bounded frontmatter, never interprets the Markdown instructions, and never rewrites
the file. Description metadata is informative, not an additional installation requirement.
Frontmatter is limited to 65,536 decoded characters and 32 collection levels. Explicit YAML tags,
anchors, and aliases are not supported by the description reader; they produce a visible metadata
warning rather than preventing installation.

## How it works

1. `dotnet list <target> package --format json` — the resolved direct packages.
2. `dotnet nuget locals global-packages --list` — where restore extracted them. `NUGET_PACKAGES`
   and `--global-packages` take precedence, in that order.
3. For each package, look in `<global-packages>/<id>/<version>/skills/`.
4. Copy each `skills/<name>/` folder to `<destination>/<name>/`, skipping and warning on collisions.

Nothing inside a skill is read or interpreted. The package author decides what a skill contains;
this tool only puts it where an agent will look.

### It copies, it never moves

The global packages folder is NuGet's content-addressable cache. It is validated during restore
and shared by every project on the machine, so moving files out of it can make restore treat the
cached package as corrupt — and would strip the skill from every other repository using that
package.

### Removal is manifest-driven

`.dotnet-package-skills.json` records exactly what was copied in. Pruning and `uninstall` act only
on paths listed there, never on whatever happens to be in the destination folder, so skills you
wrote yourself are never at risk of being deleted.

If that manifest exists but cannot be read, `install` and `uninstall` stop without changing
anything and preserve the file for repair. Resolve any merge conflict or restore it from source
control before retrying. If it cannot be recovered, move the whole destination folder aside before
installing again; the tool will not guess which existing folders it owns.
Missing ownership data and duplicate skill claims are also treated as damaged manifests.
Concurrent tool operations on the same destination are serialized, and an interactive choice
is rejected if ownership changed before it could be applied.

## A note on trust

A bundled skill is a set of instructions written by a third party that your agent will then
follow. That is a supply-chain surface. This tool only ever copies from packages your project
already depends on, and it prints every skill it copied so you can review them. Treat a new skill
the way you would treat any new dependency.

## Troubleshooting

**"No bundled skills found"** — the common and correct outcome; most packages do not ship skills.

**"resolved but not extracted in the NuGet cache"** — run `dotnet restore` and try again. This
also happens when packages come from a NuGet *fallback folder* (common in containers and on hosted
build agents); point `--global-packages` at that folder.

**"Unrecognized option '--format'"** — the SDK predates 7.0.200. Upgrade it.

**Wrong global packages folder** — nuget.config discovery walks up from the current directory, so
run the tool from your repository root, or pass `--global-packages` explicitly.

**Solution filters (`.slnf`)** are not accepted by `dotnet list package` on all SDKs. Pass the
underlying `.sln`, or run once per project with `--target`.

## Building from source

```bash
dotnet test
dotnet pack src/DotnetPackageSkills -c Release -o ./artifacts
dotnet tool install --global --add-source ./artifacts dotnet-package-skills
```

## License

MIT
