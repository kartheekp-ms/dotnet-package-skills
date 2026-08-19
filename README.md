# dotnet-package-skills

Copies agent skills bundled inside NuGet packages into a folder your coding agent actually reads.

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
dotnet package-skills sync
```

That is the whole workflow. It finds your solution or project, lists its packages, locates each
direct dependency in the NuGet cache, and copies any bundled skills into `.agents/skills/`.

Run it again after adding or upgrading packages. It is idempotent: it refreshes what is current
and removes what is not.

### Commands

| Command | What it does |
| --- | --- |
| `sync` | Copy bundled skills into the destination, and remove stale skills no longer provided by direct dependencies. |
| `list` | Show which packages ship skills, without copying anything. |
| `uninstall` | Remove skills this tool copied in. |

### What to point it at

Three ways to say which packages to take skills from:

```bash
dotnet package-skills sync                              # auto-detect solution or project
dotnet package-skills sync --target src/MyApp.slnx      # a specific solution or project
dotnet package-skills sync --package Mockly@1.10.0      # exact packages, no project needed
```

`--package` is repeatable and needs an **exact version** — `Mockly@1.*` and `Mockly@[1.0,2.0)` are
refused. Resolving a range means picking a version, and the only correct answer to "which version"
comes from a project's restore, which is what `--target` is for. Guessing would copy skills
describing a release you do not actually reference.

`--target` and `--package` cannot be combined; both answer the same question.

Naming packages explicitly is **additive** — it copies what you asked for and leaves everything
else alone. Only a target describes a complete set of packages, so only a target prunes.

### Choosing which skills to install

By default `sync` copies everything it finds. Add `--interactive` to choose:

```bash
dotnet package-skills sync --interactive
```

```
Skills for MyApp.slnx                                       page 1 of 3

  up/down move   left/right page   space toggle
  a all   c none   enter confirm   esc cancel

> [x] contoso.widgets-widget-usage      installed  Contoso.Widgets 2.3.0
  [ ] contoso.widgets-widget-testing    new        Contoso.Widgets 2.3.0
  [x] mockly-usage                      installed  Mockly 1.10.0
  [ ] serilog-console-guidance          new        Serilog.Sinks.Console 5.0.1

  2 of 24 selected   1 to remove
```

Ten skills a page, because a list long enough to scroll off the top is a list nobody reads before
agreeing to it. Skills you already have start selected, so pressing enter straight away changes
nothing and a new skill is always an explicit opt-in.

**Turning off a skill you already have deletes it** — including under `--package`, where sync is
otherwise additive. Pruning is inferred from a complete package set; deselecting is you saying so.

`--interactive` needs a terminal and cannot be combined with `--json`. Pair it with `--dry-run` to
see what a selection would change before committing to it.

### Options

| Option | Applies to | Description |
| --- | --- | --- |
| `-t, --target <PATH>` | sync, list | Solution or project to inspect. Defaults to searching the current directory. |
| `-p, --package <ID@VERSION>` | sync, list | Take skills from an exact package instead of a project. Repeatable. No floating versions. |
| `-d, --destination <PATH>` | all | Where skills are copied. Default `.agents/skills`. |
| `--no-restore` | sync, list | Fail instead of restoring when the target has not been restored. |
| `--global-packages <PATH>` | sync, list | Override the NuGet global packages folder. |
| `-i, --interactive` | sync | Choose which skills to install, a page at a time. Not with `--json`. |
| `-p, --package <ID[@VERSION]>` | uninstall | Remove only skills from this package — every version, or one. |
| `--dry-run` | sync, uninstall | Report what would change without writing anything. |
| `--json` | all | Machine-readable output, for scripts and agents. |

### Targeting another agent's folder

`.agents/skills` is the vendor-neutral default. Point `--destination` anywhere else:

```bash
dotnet package-skills sync --destination .claude/skills
dotnet package-skills sync --destination .codex/skills
```

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

Package authors should prefix every folder with their lowercased package id, as shown above. This
keeps names globally unique when skills from many packages share one destination. The convention is
documented rather than enforced, so existing safe names still work.

### Name collisions

Destination names are compared case-insensitively. If two package skills choose the same name, the
first one in deterministic package order is copied and later collisions are skipped with a warning.
An existing destination folder not tracked by this tool is treated as user-owned and is also
skipped, never overwritten.

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
