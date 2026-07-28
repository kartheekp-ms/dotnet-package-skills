# dotnet-package-skills

Copies agent skills bundled inside NuGet packages into a folder your coding agent actually reads.

## The problem

Package authors are the domain experts on their own libraries, and some of them now ship an
**agent skill** inside the package — instructions covering the conventions, gotchas, and correct
usage patterns for that library. Those files are packed at `skills/<skill-name>/SKILL.md`.

Restore extracts the package into the **NuGet global packages folder** (`~/.nuget/packages` by
default), which lives outside your repository and is shared by every project on the machine.
Coding agents only scan a skills directory *inside* the working repo. So the skill is on disk,
correct, and invisible.

This tool bridges that gap.

```
~/.nuget/packages/mockly/1.10.0/skills/mockly/SKILL.md     ← where restore puts it
        ↓
.agents/skills/mockly/1.10.0/SKILL.md                      ← where your agent looks
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
one in the NuGet cache, and copies any bundled skills into `.agents/skills/`.

Run it again after adding or upgrading packages. It is idempotent: it refreshes what is current
and removes what is not.

### Commands

| Command | What it does |
| --- | --- |
| `sync` | Copy bundled skills into the destination, and remove ones left over from earlier package versions. |
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

### Options

| Option | Applies to | Description |
| --- | --- | --- |
| `-t, --target <PATH>` | sync, list | Solution or project to inspect. Defaults to searching the current directory. |
| `-p, --package <ID@VERSION>` | sync, list | Take skills from an exact package instead of a project. Repeatable. No floating versions. |
| `-d, --destination <PATH>` | all | Where skills are copied. Default `.agents/skills`. |
| `--include-transitive` | sync, list | Scan the whole dependency graph, not just direct `PackageReference`s. |
| `--no-restore` | sync, list | Fail instead of restoring when the target has not been restored. |
| `--global-packages <PATH>` | sync, list | Override the NuGet global packages folder. |
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

Skills land at `<destination>/<package-id>/<version>/`:

```
.agents/skills/
├── .dotnet-package-skills.json     # what this tool copied in; do not hand-edit
└── mockly/1.10.0/
    └── SKILL.md
```

Package id and version are part of the path deliberately: two packages can ship a skill with the
same name without colliding, and anyone reading the tree can tell where a skill came from and
which version it documents.

A package that ships **more than one** skill gets a folder per skill, since they would otherwise
overwrite each other:

```
.agents/skills/contoso.widgets/2.3.0/
├── widget-usage/
│   ├── SKILL.md
│   └── references/batching.md
└── widget-testing/
    └── SKILL.md
```

### Solutions where projects disagree on a version

If two projects in the same solution reference different versions of the same package, you get a
folder for **each version**:

```
.agents/skills/mockly/
├── 1.10.0/SKILL.md     # what src/Api references
└── 1.11.0/SKILL.md     # what src/Worker references
```

Both versions are genuinely in use and each skill documents its own release, so neither can be
dropped. When every project moves to one version, the next `sync` prunes the one that is no
longer referenced.

### Should I commit this folder?

Either is defensible. Commit it so the whole team and CI get the skills without running anything,
or gitignore it and let each machine refresh it. Pick one and say so in your contributing guide.

## For package authors: shipping a skill

Put the skill under `skills/<skill-name>/` in your project and pack it:

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

Verify what you shipped before publishing — the package is just a zip:

```bash
unzip -l bin/Release/Contoso.Widgets.2.3.0.nupkg | grep skills
```

Both layouts are recognised: `skills/<name>/SKILL.md` (preferred, and required if you ship more
than one skill), and a lone `skills/SKILL.md`, which takes your package id as its name.

## How it works

1. `dotnet list <target> package --format json` — the resolved package graph.
2. `dotnet nuget locals global-packages --list` — where restore extracted them. `NUGET_PACKAGES`
   and `--global-packages` take precedence, in that order.
3. For each package, look in `<global-packages>/<id>/<version>/skills/`.
4. Copy to `<destination>/<id>/<version>/`, or to `<destination>/<id>/<version>/<skill>/` when the
   package ships more than one skill.

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
