# Contributing

Thanks for helping out. This is a small, deliberately boring tool — the bar for changes is that
they keep it small and boring.

## Getting set up

You need the .NET SDK 10.0 or later to build (the tool itself ships for net8.0 and net10.0).

```bash
git clone <repo>
cd dotnet-package-skills
dotnet build
dotnet test
```

Try your build against a real repository without installing it:

```bash
dotnet run --project src/DotnetPackageSkills -f net10.0 -- list --target /path/to/YourApp.sln
```

Install your build as a global tool when you want to exercise the `dotnet package-skills` verb
itself:

```bash
dotnet pack src/DotnetPackageSkills -c Release -o ./artifacts
dotnet tool uninstall --global dotnet-package-skills
dotnet tool install --global --add-source ./artifacts dotnet-package-skills
```

## Layout

```
src/DotnetPackageSkills/
├── Program.cs              CLI surface: commands, options, exit codes
├── SkillSyncService.cs     Orchestration — the only place the steps are sequenced
├── Cli/OutputWriter.cs     Human-readable and JSON rendering
├── Infrastructure/         Process execution and the dotnet CLI wrapper
├── NuGet/                  Target detection, package listing, cache path resolution
└── Skills/                 Discovery, copying, pruning, the install manifest

tests/DotnetPackageSkills.Tests/    xunit; no network, no dotnet invocations
samples/Contoso.Widgets/            Example of a package that ships a skill
```

## Invariants

These are the things worth being careful about. Each exists for a reason that is not obvious from
the code alone, so please don't quietly change them.

**Copy from the global packages folder; never move.** It is NuGet's content-addressable cache,
validated during restore and shared by every project on the machine. Moving files out can make
restore treat a cached package as corrupt, and removes the skill from every other repository using
that package.

**Removal is driven by the manifest, never by scanning the destination.** `.dotnet-package-skills.json`
records what this tool copied in; pruning and `uninstall` act only on those paths. Users keep their
own hand-written skills in the same folder, and deleting one of those would be unforgivable.

**Don't read or interpret skill contents.** The tool identifies skill folders by structure and
copies them. What a skill contains is the package author's business. An earlier version parsed
SKILL.md frontmatter to show descriptions; it was removed because it added a YAML-shaped parsing
problem to a file-copying tool.

**Skill names from packages are untrusted input.** They become path segments in the user's repo.
`SkillDiscovery.IsSafeSkillName` is the gate; keep it strict.

**Only a target licenses pruning.** `--target` describes a complete set of packages, so anything
missing from it is genuinely no longer referenced. `--package` names a few packages and says
nothing about the rest, so it copies additively — see the `prune` parameter on
`SkillInstaller.Install`. Getting this backwards would delete a user's other skills the first time
they synced a single package.

**`--package` refuses floating versions and ranges.** Resolving one means choosing a version, and
the only correct answer comes from a project's restore. `PackageCoordinate.Parse` is the gate.

**Only direct dependencies are scanned.** Applications code against their direct package
references, not implementation details brought in transitively. Do not add `--include-transitive`
or parse `transitivePackages` without revisiting that product decision.

**The authored skill folder name is the destination folder name.** A package skill at
`skills/contoso.widgets-widget-usage/` lands at
`<destination>/contoso.widgets-widget-usage/`. Package and version remain manifest metadata; they
do not create destination path segments. A skill must be an immediate subdirectory containing
`SKILL.md`; a lone `skills/SKILL.md` is intentionally unsupported.

**Collisions warn and skip; they never overwrite silently.** Destination names compare
case-insensitively. Package enumeration and skill discovery stay deterministic so the first match
wins reproducibly. An existing untracked destination folder is user-owned and untouchable.
Package authors avoid collisions by prefixing skill folders with their lowercased package ID, but
the tool does not enforce that naming convention.

**Side-by-side package versions are not represented in the destination.** Repositories are expected
to align package versions with NuGet Central Package Management. `PackageLister.Parse` still keeps
distinct `(id, version)` pairs so multiple versions produce a visible collision warning instead of
a silent overwrite.

**Errors should read as guidance.** Throw `PackageSkillsException` with a message that tells the
user what to do next. `Program.cs` prints it without a stack trace. If a message would leave
someone stuck, it needs more words.

## Tests

Tests run offline and never invoke `dotnet`. Anything that needs the CLI goes through
`IProcessRunner`, which `SkillSyncServiceTests` fakes — see `FakeDotnet` there for the pattern.
Use `TempDirectory` for anything touching the file system; it cleans up after itself.

Name tests as a sentence describing the behaviour, not the method under test:

```csharp
[Fact]
public void Sync_skips_a_later_skill_when_destination_names_collide()
```

New behaviour needs a test. Bug fixes need a test that fails without the fix — the `.slnx`
preference bug shipped with one, and that is why it stayed fixed.

## Style

`TreatWarningsAsErrors` is on; builds must be warning-clean. Beyond that, match the surrounding
code. Comments explain *why*, not what — if a comment restates the code, delete it.

One naming note so it doesn't look like drift: the CLI verb is `sync`, but the types that write
files keep `Install` names — `SkillInstaller.Install`, `InstallManifest`. The verb describes what
the user asked for; the types describe what they do to the file system. Both are right at their own
level.

## Compatibility

- The tool targets `net8.0` and `net10.0`. Don't drop `net8.0` without a discussion; it is the LTS
  a lot of teams are still on.
- `dotnet list package --format json` requires SDK 7.0.200+. That is the floor for what the tool
  can inspect, and the error message says so when it isn't met.
- Output of `dotnet nuget locals` has changed shape across SDK versions. Parsing keys off the
  `global-packages:` label rather than line position — keep it that way.

## Pull requests

- One change per PR.
- `dotnet build` and `dotnet test` pass.
- README updated if you changed the CLI surface.
- Say what you tested it against. "Ran `sync` on a solution with 40 packages, two of which ship
  skills" is worth more than a description of the diff.
