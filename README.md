# dotnet-package-skills

Copies agent skills bundled inside NuGet packages into a folder your coding agent actually reads.

For a product-oriented command reference and sample outputs, see the
[functional specification](docs/functional-spec.md). For the expected behavior in each situation,
such as a package upgrade or a package leaving the project, see [scenarios](docs/scenarios.md).

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
dotnet-package-skills install
```

That is the whole workflow. It finds your solution or project, lists its packages, locates each
direct dependency in the NuGet cache, and copies any bundled skills into `.agents/skills/`.

Run it again after adding or upgrading packages. It refreshes the skills of the packages it finds,
and when a package moves to a new version, it removes the skills that version no longer ships. It
never removes skills because a package left the project. Instead, it lists them, and
`dotnet-package-skills uninstall --stale` removes them.

### Commands

| Command | What it does |
| --- | --- |
| `install` | Copy bundled skills into the destination. Add `--interactive` to choose which new skills to add. |
| `list` | Show which packages ship skills, without copying anything. |
| `uninstall` | Remove skills this tool copied in. Add `--stale` to remove only the skills the project no longer references, or `--interactive` to pick them. |

### What to point it at

Three ways to say which packages to take skills from:

```bash
dotnet-package-skills install                              # auto-detect solution or project
dotnet-package-skills install --target src/MyApp.slnx      # a specific solution or project
dotnet-package-skills install --package Mockly@1.10.0      # exact packages, no project needed
```

`--package` is repeatable and needs an **exact version** — `Mockly@1.*` and `Mockly@[1.0,2.0)` are
refused. Resolving a range means picking a version, and the only correct answer to "which version"
comes from a project's restore, which is what `--target` is for. Guessing would copy skills
describing a release you do not actually reference.

`--target` and `--package` cannot be combined; both answer the same question.

Naming packages explicitly touches only the packages you name and leaves every other installed
skill alone. A target describes the project's complete set of packages, so a target install can
also tell you which installed skills belong to packages the project no longer references. If a
package the target resolves is missing from the NuGet cache, `install` stops before changing
anything; restore first.

The tool expects one version of each package, which is what
[Central Package Management](https://learn.microsoft.com/nuget/consume-packages/central-package-management)
gives a repository. When the target resolves two versions of the same package, or `--package`
names two, `install` stops without changing anything and names the versions to align.

### Keeping skills in step with the project

When a package moves to a new version, `install` copies the new version's skills over the old ones
and removes any skill the new version no longer ships. The manifest records one version per
package, so it always says which release the installed guidance describes.

When a package leaves the project, `install` keeps its skills and lists them:

```
2 installed skills belong to a package that the target no longer references:
  fabrikam.testing-fakes (fabrikam.testing 1.4.0)
  fabrikam.testing-fixtures (fabrikam.testing 1.4.0)
Run 'dotnet-package-skills uninstall --stale' to remove them.
```

The suggested command repeats the `--target` and `--destination` you passed, so you can run it as
printed. The same goes for every command that the tool's errors suggest.

A reference can disappear for a moment, for example halfway through a refactor, so removing
skills is always a command you run on purpose. `uninstall --stale` removes every **stale** skill:
one whose package the target no longer references, or references at a different version. Preview
it with `--dry-run`, or pick among the stale skills with `--interactive`:

```bash
dotnet-package-skills uninstall --stale --dry-run
dotnet-package-skills uninstall --stale
```

`--stale` reads the project's package references, so it needs a solution or project: the one in
the current directory, or the one you pass with `--target`. Skills you added with
`install --package` for packages outside the project count as stale too.

### Choosing which skills to install

By default `install` copies everything it finds. Add `--interactive` to choose which new skills to
add:

```bash
dotnet-package-skills install --interactive                          # everything the project references
dotnet-package-skills install --package Mockly@1.10.0 --interactive  # just one package's skills
```

It composes with `--target` and `--package`, so you can narrow to a single package first and then
pick among the skills it ships — which is what you want when one package bundles a dozen of them.

```
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

The checklist lists only skills that aren't installed, and nothing starts checked, so accepting
adds exactly the skills you ticked. An interactive install never refreshes or removes anything:
run `install` without `--interactive` to refresh, and `uninstall` to remove. When every skill the
packages ship is already installed, it prints `Nothing new to install.` and doesn't open the
checklist. Skills it can't add, such as a name already taken by another package or by a folder you
wrote yourself, aren't listed; the report names them under the skipped warning.

Adding only makes sense when the installed skills match the packages, so before the checklist
opens, an interactive install stops without changing anything when:

- the target resolves more than one version of a package, or `--package` names more than one;
- with a target, a package the target resolves is missing from the NuGet cache;
- with a target, an installed skill is stale. Run `dotnet-package-skills uninstall --stale` first;
- with `--package`, a named package is installed at another version. Run
  `dotnet-package-skills uninstall --package <ID>` first, or run `install --package <ID>@<VERSION>`
  without `--interactive` to move it to the new version.

Each description follows the authored skill name immediately after ` - `, without a padded
column or a package/version suffix. Package prefixes in authored names are kept, and continuation
lines flow beneath the skill text, using the available width rather than leaving a name-sized gap.
The focused skill's text, including wrapped description lines, is blue. Checked items have a blue
`X`; other skill names and descriptions use their normal color. The summary counts the checked
skills; there is no separate status column. With `NO_COLOR` set, or on a terminal without color
support, `>` marks the focused skill and `[X]` the checked ones, with nothing else beside them.

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
do not leave a screenful of blank rows, and a single page has no page counter. The note under the
title gives way when the window is too small to fit it, so a small window keeps the checklist
rather than refusing to open.
The live picker uses a temporary terminal screen and starts at its top, regardless of the shell's
previous cursor position. Host-driven reflow cannot leave duplicate copies in normal scrollback.
Accepting, cancelling, or a handled failure restores the previous shell screen; the final report
is written there, not alongside an old checklist.

Descriptions come from the top-level YAML `description` in each package's `SKILL.md`. Missing
descriptions say `No description provided.`; unreadable or malformed metadata shows an explicit
description warning without hiding the skill or preventing its selection. Only the interactive
checklists read this metadata; reports and the ownership manifest don't include descriptions.

`--interactive` needs a terminal. Pair it with `--dry-run` to see what a selection would change
before committing to it.

### Choosing what to remove

`uninstall` takes `--interactive` too, and lists only what this tool installed — never a skill you
wrote yourself, because it reads the manifest rather than the folder:

```bash
dotnet-package-skills uninstall --interactive
```

```
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
Blue X: selected
```

Nothing starts ticked, so a mistaken enter removes nothing. A ticked row looks the same as in the
install checklist: each checklist does only one thing, so the title and the summary say what a
tick does. Narrow the list first with `--package` if you only care about one package, or with
`--stale` to see only the skills that no longer match the project:

```
Which skills should be uninstalled?
Only skills that don't match the target are listed.

> [X] fabrikam.testing-fakes - Create fakes and verify their calls in unit
      tests.
  [ ] fabrikam.testing-fixtures - Share expensive setup across tests with
      fixtures.

1 of 2 selected; 1 to remove
```

Add `--dry-run` to see the outcome without it happening. Descriptions are read from the installed
copies, not from the NuGet cache. A missing or damaged `SKILL.md` does not prevent removal of a
manifest-owned skill. Package matching ignores case, and version filters are normalized in both
modes (`1.10` matches `1.10.0`). Blank, missing, or repeated uninstall `--package` values are
errors, not an unfiltered uninstall. `--stale` and `--package` can't be combined.

### Options

| Option | Applies to | Description |
| --- | --- | --- |
| `-t, --target <PATH>` | install, list | Solution or project to inspect. Defaults to searching the current directory. |
| `-p, --package <ID@VERSION>` | install, list | Take skills from an exact package instead of a project. Repeatable. No floating versions. |
| `-d, --destination <PATH>` | install, list | Where skills are copied. Default `.agents/skills`. |
| `-d, --destination <PATH>` | uninstall | Where to remove them from. Must match the one you installed to. |
| `--no-restore` | install, list | Fail instead of restoring when the target has not been restored. |
| `--global-packages <PATH>` | install, list | Override the NuGet global packages folder. |
| `-i, --interactive` | install | Choose which new skills to add, with descriptions and pagination. Lists only skills that aren't installed. Combines with `--target` or `--package`. |
| `-i, --interactive` | uninstall | Choose which installed skills to remove, with descriptions and pagination. Lists only what this tool installed. |
| `-p, --package <ID[@VERSION]>` | uninstall | Remove only this package's skills — whichever version is installed, or only if it's the version you name. |
| `--stale` | uninstall | Remove only stale skills: those whose package the target no longer references, or references at a different version. Needs a solution or project. Not with `--package`. |
| `-t, --target <PATH>` | uninstall | With `--stale`, the solution or project to compare against. Defaults to searching the current directory. |
| `--no-restore` | uninstall | With `--stale`, fail instead of restoring when the target has not been restored. |
| `--dry-run` | install, uninstall | Report what would change without writing anything. |

### Targeting another agent's folder

`.agents/skills` is the vendor-neutral default. Point `--destination` anywhere else:

```bash
dotnet-package-skills install --destination .claude/skills
dotnet-package-skills install --destination .codex/skills
```

`uninstall` takes the same option, and needs it: it only looks where you point it, so removing
what you put in `.claude/skills` means saying so again.

```bash
dotnet-package-skills uninstall --destination .claude/skills
```

### Scripts and CI

Reports are written for people, and the manifest is the only machine-readable output. In a
script, rely on the exit code: `0` when the command succeeded, and `1` when it stopped, with the
reason on stderr. A command that stops changes nothing. Argument errors also print help on stdout.

A job that keeps a committed skills folder in step with the project can run:

```bash
dotnet-package-skills install
dotnet-package-skills uninstall --stale
```

To see what's installed, read `.agents/skills/.dotnet-package-skills.json`, described in
[What you get](#what-you-get). `--interactive` needs a terminal, so leave it out of scripts.

Human-readable reports and diagnostics, including argument-validation errors and parser suggestions,
remove terminal escape sequences and unsafe control characters from metadata, paths, and diagnostic
text. This is display-only: arguments are validated as supplied, and stored identities are
unchanged.

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

The manifest follows the shape of the .NET local tool manifest (`dotnet-tools.json`): a format
`version`, then one entry per package, keyed by its lowercase package ID, with the one version its
skills came from and the skill folders it owns:

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

The file is safe to commit. The tool writes it the same way on every platform: UTF-8 without a
byte order mark, LF line endings, and entries in a stable order, so a Windows checkout and a Linux
checkout produce the same bytes. It ignores properties it doesn't recognize, and it refuses a
manifest with a newer format `version` and asks you to update the tool.

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

One combination stops `install` instead: the owner's package moves to a version that no longer
ships the skill, while another package ships a skill with that name. Removing the old copy would
hand the name over, and the manifest can't keep an older version's copy under the new version, so
`install` changes nothing and suggests `uninstall --package <ID>` for the owner. After that,
`install` copies both packages' current skills.

V1 does not reconcile distinct physical case variants on case-sensitive filesystems. Keep authored
skill-folder casing stable across versions and avoid folders such as `guide` and `GUIDE` in the
same destination. Case-only renames or collisions between those physical variants can leave
untracked old copies or overwrite a handwritten variant; those scenarios are outside v1 guarantees.

Refreshing a tracked skill replaces its entire folder, including local edits and added files.
Keep hand-written guidance in separate, untracked skill folders.

### Package versions

The destination holds skills from one version of each package, and the manifest records that
version. Keep the projects in a repository on one version of each package, which is what
[NuGet Central Package Management](https://learn.microsoft.com/nuget/consume-packages/central-package-management)
does. When a target resolves more than one version of a package, `install` stops without changing
anything and names the versions to align; `--package` with two versions of one package stops the
same way. `list` still shows every version it finds.

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
3. `install` stops without changing anything if a package resolves to more than one version, or
   if a package the target resolves is missing from the cache.
4. For each package, look in `<global-packages>/<id>/<version>/skills/`.
5. Copy each `skills/<name>/` folder to `<destination>/<name>/`, skipping and warning on collisions.
   For a package that moved to a new version, remove the skills the new version no longer ships.
6. Record what was copied in `<destination>/.dotnet-package-skills.json`.

`uninstall --stale` needs only step 1: it compares the manifest with the target's package
references and never looks in the NuGet cache for skills.

Nothing inside a skill is read or interpreted. The package author decides what a skill contains;
this tool only puts it where an agent will look.

### It copies, it never moves

The global packages folder is NuGet's content-addressable cache. It is validated during restore
and shared by every project on the machine, so moving files out of it can make restore treat the
cached package as corrupt — and would strip the skill from every other repository using that
package.

### Removal is manifest-driven

`.dotnet-package-skills.json` records what was copied in. `install` (when a package moves to a new
version) and `uninstall` remove only paths listed there, rather than scanning arbitrary folders.
Keep hand-written guidance in separate, untracked folders, subject to the v1 case-variant and
linked-manifest limitations described here.

If that manifest exists but cannot be read, `install` and `uninstall` stop without changing
anything and preserve the file for repair. Resolve any merge conflict or restore it from source
control before retrying. If it cannot be recovered, move the whole destination folder aside before
installing again; the tool will not guess which existing folders it owns.
A manifest is also refused when it names a newer format version (update the tool), when it was
written by a pre-release build of this tool (move the skills folder aside and install again), or
when a package is missing its version, a package ID is invalid, or a skill is claimed twice.
Skill names must identify a single folder directly inside the destination. Names ending in a dot
or space, including `...`, are rejected because Windows can resolve them to another folder or the
destination itself. A manifest containing such a name blocks install and uninstall, including
interactive and dry-run modes, before any skill files or manifest bytes are changed.
The tool creates an ordinary manifest file by default and updates an existing manifest in place.
Symbolic-link or other redirected manifests are unsupported in v1. The tool does not create those
links or protect their targets: normal filesystem operations may follow a link, including one
already present in a checked-out repository. Use a regular manifest file in the skills destination;
customers who provide links are responsible for their effects.
Concurrent tool operations on the same destination are serialized, and an interactive choice
is rejected if ownership changed before it could be applied.

## A note on trust

A bundled skill is a set of instructions written by a third party that your agent will then
follow. That is a supply-chain surface. This tool only ever copies from packages your project
already depends on, and it prints every skill it copied so you can review them. Treat a new skill
the way you would treat any new dependency.

## Troubleshooting

**"No bundled skills found"** — the common and correct outcome; most packages do not ship skills.

**"resolved packages are missing from"** the NuGet cache — run `dotnet restore` for the target and
try again. This also happens when packages come from a NuGet *fallback folder* (common in
containers and on hosted build agents); point `--global-packages` at that folder.

**"resolve to more than one version"** — projects in the target reference different versions of a
package. Align them, for example with Central Package Management, and try again.

**"installed skills don't match the target"** (from `install --interactive`) — some installed
skills are stale. Preview them with `dotnet-package-skills uninstall --stale --dry-run`, remove
them with `uninstall --stale`, and try again.

**"is already installed, and an interactive install only adds skills"** — `install --interactive
--package` named a package that is installed at another version. Run `install --package` without
`--interactive` to move it to the new version, or `uninstall --package <ID>` first.

**"Could not read the install manifest"** — the manifest has a merge conflict or was edited into a
shape the tool can't trust. See [Removal is manifest-driven](#removal-is-manifest-driven).

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
