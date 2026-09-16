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
├── SkillInstallService.cs     Orchestration — the only place the steps are sequenced
├── Cli/OutputWriter.cs     Human-readable and JSON rendering
├── Cli/SkillPicker.cs      The --interactive picker, paged so one screen is one page
├── Cli/ITerminal.cs        Console access behind an interface, so the picker can be tested
├── Cli/InteractiveSkills.cs  Picker-only metadata and selection mapping
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
groups copied skill folder names under their package id and version; pruning and `uninstall` act
only on those names. Users keep their own hand-written skills in the same folder, and deleting one
of those would be unforgivable.

**An unreadable manifest stops every operation that needs ownership.** Never treat a malformed or
unreadable manifest as empty. Empty means the existing folders are user-owned; corrupt means their
ownership is unknown. `install` and `uninstall` must fail before changing anything and preserve the
file so the user can repair or restore it. `list` may still run because it does not read ownership
or write anything.
An existing manifest must contain `installed`, and JSON properties and case-insensitive
destination claims must be unique.

**No tracked skills means no manifest and no folder.** When the last entry goes, `install` and
`uninstall` both delete `.dotnet-package-skills.json` and drop the destination folder if it is
empty, so a repository where nothing ships a skill never grows a stray `.agents/skills/`. The
folder only goes when it is genuinely empty — hand-written skills keep it alive.

**Descriptions are read-only presentation metadata, not installation requirements.** Discovery
still identifies skills by folder structure. Only interactive pickers read the top-level YAML
`description` in `SKILL.md`, using a bounded frontmatter reader and an established YAML parser.
Never interpret the Markdown body, execute metadata, invent a description, or rewrite the file.
Missing metadata gets an explicit placeholder; unreadable or invalid metadata gets a visible
warning without hiding the skill. This intentionally replaces the former no-frontmatter-parsing
rule so users can make an informed selection. Regular reports, JSON, and manifests are unchanged.

**Skill names from packages are untrusted input.** They become path segments in the user's repo.
`SkillDiscovery.IsSafeSkillName` is the gate; keep it strict.

**Only complete, noninteractive target discovery licenses automatic pruning.** `--package` says
nothing about unrelated installed skills, and an interactive picker may remove only explicitly
deselected rows. A missing resolved target package stops installation before any writes, including
previews; an incomplete cache must never look like permission to delete skills.

**A deselection removes; an omission does not.** `--interactive` hands `SkillInstaller.Install` a
`deselected` set of destination paths, and those go even when `prune` is false. That is not a hole
in the rule above: pruning is inferred from a complete package set, whereas a user turning a skill
off is a direct instruction about that skill. Only paths the picker actually displayed may go in
that set, so interactive mode can never remove something it did not show.

**The picker pages, and that is the point.** A solution can reference many packages that ship
skills. `SkillPicker` renders a frame that fits the window and redraws it in place, so the list
can never scroll off the top unread — agreeing to skills you did not see is the failure mode worth
designing against. Page size follows rendered height, including descriptions and help, rather
than an item-count ceiling. The picker itself has no filesystem access: `InteractiveSkills`
supplies package descriptions for install and installed-file descriptions for uninstall.

**The frame is measured from its contents and bounded by the window.** Names and descriptions
share a row with ` - ` immediately after the authored name, not a padded name column. Do not append
package/version metadata or strip the package prefix from the name. Each skill's wrapped
description continues at the skill-text edge and uses the remaining row width, not an indent
as wide as the name. Measure those lines and every
wrapped footer before assigning whole skill entries to pages. An oversized description must be
scrollable, never silently truncated. Reflow on resize while preserving focus and selections.
Page boundaries must not shift just because a checkbox or cursor changed. Partial last pages end
at their actual content rather than a run of blank rows.

Two things follow from redrawing in place, and both are easy to break. Rows are padded to the
measured width, and rows below a shorter frame are blanked, because overwriting is the only way
to erase without ANSI. And the widest possible summary is measured rather than the current one,
since the removal clause appears and disappears as you select. Chrome that would do nothing is
dropped: no counter on a single page, no movement or select-all keys for a single skill.

**Keyboard hints follow Aspire's checklist style.** The primary hint is
`(Press <space> to select, <enter> to accept)`, below the list. Use the same angle-bracket key
notation for paging, select-all, clear-all, cancel, and description scrolling, beginning each
keyboard-help line with `Press`. Wrap help rather than clipping away the keys. Only advertise
paging and scrolling when they are useful.

**Focus and pending actions are separate cues.** Blue identifies the focus marker, green marks a
pending installation, and red marks a pending removal. An installed skill being kept is neutral.
Do not paint a whole focused row blue and obscure its action color. Descriptions stay neutral,
and a summary counts pending actions. No-color terminals use compact `+`/`-` action markers
instead; respect `NO_COLOR`. A dedicated status column would take space away from descriptions.

**A tick means the opposite thing in each picker, and that is deliberate.** Installing, it keeps
the skill, so what is already there starts ticked and pressing enter changes nothing.
Uninstalling, it deletes, so nothing starts ticked and pressing enter still changes nothing.
`PickerMode` carries the difference; the safe default in both is that confirming without touching
anything is a no-op. The uninstall list comes from the manifest, so a skill someone wrote by hand
is never offered for deletion.

**The picker owns the terminal, so it has to hand it back.** `Choose` hides the cursor and takes
Ctrl+C as input, and restores both in a `finally`. Ctrl+C is why: left to the runtime it ends the
process mid-frame, so the restore never runs and the user is left typing into a terminal with no
cursor. Taken as a key it cancels through the same path as `esc`. Note the modifier is tested
before the switch, because a bare `c` clears the selection.

**Retained copies stay checked until explicitly deselected.** Target-based install pickers include
manifest-owned skills no longer supplied by the target, even when discovery has zero candidates.
Their descriptions come from the installed copies. Ownership snapshots are rechecked before
applying an interactive choice; concurrent ownership changes invalidate it rather than changing
which package's files are affected.
Hold the destination lock from ownership loading through the final manifest write. Both
installation and uninstallation participate, so another tool invocation cannot change ownership
between the check and mutation. Canonicalize destination aliases before choosing the lock.

**Resizing invalidates an in-progress frame.** Read width and height together, restart a redraw
if its viewport changes, and clear cells in place rather than scrolling blank lines. Otherwise
old picker copies accumulate in terminal history and can wrap incorrectly when the host resizes.
Preserve prior scrollback, focus, and selections; never swallow unrelated rendering failures.

Every render also parks the cursor directly below the last line it drew, rather than at the bottom
of the rows the frame reserved. That is what the shell prompt lands on if the process dies without
unwinding — `SkillPickerTests` pins it, and the assertion fails if the parking is removed.

**Picker chrome is ASCII; author text is not restricted to English.** Keep control hints and
markers ASCII so legacy console encodings do not lose them. Display Unicode descriptions without
splitting text elements, measuring terminal cells rather than UTF-16 code units. Strip unsafe
terminal control sequences from author-supplied text. All color goes through `ITerminal`, and the
picker uses BOM-less UTF-8 while prompting. Restore the original encoding and terminal styling
when it exits or fails, so ordinary command output retains its existing behavior.

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
Different packages cannot transfer an already-tracked destination between owners in any install
mode. A skipped conflicting path is protected from pruning as well as copying. Same-package
version refreshes remain allowed.
Keep all discovery candidates internally until install-time ownership is known. Prefer the
current owner's candidate; `list` remains a destination-independent discovery report.
Package authors avoid collisions by prefixing skill folders with their lowercased package ID, but
the tool does not enforce that naming convention.

**Package filters must not broaden destructive operations.** An explicitly blank filter is an
error; only an absent `--package` means all packages. Interactive and noninteractive uninstall
use the same normalized version matcher.

**Side-by-side package versions are not represented in the destination.** Repositories are expected
to align package versions with NuGet Central Package Management. `PackageLister.Parse` still keeps
distinct `(id, version)` pairs so multiple versions produce a visible collision warning instead of
a silent overwrite.

**Errors should read as guidance.** Throw `PackageSkillsException` with a message that tells the
user what to do next. `Program.cs` prints it without a stack trace. If a message would leave
someone stuck, it needs more words.

## Tests

Tests run offline and never invoke `dotnet`. Anything that needs the CLI goes through
`IProcessRunner`, which `SkillInstallServiceTests` fakes — see `FakeDotnet` there for the pattern.
Use `TempDirectory` for anything touching the file system; it cleans up after itself.

The interactive picker goes through `ITerminal`, which `FakeTerminal` drives from a scripted key
sequence and reads back as a screen buffer. It models a buffer rather than concatenating writes
because the picker redraws in place, so appending every write would show frames stacked on top of
each other instead of the one page a user sees.

### Real terminal regressions on Windows

The optional ConPTY suite launches the actual executable in a Windows pseudo-console, sends
keyboard input, resizes the console, and checks the rendered screen and colors. Its fixtures use
an isolated extracted-package layout and a solution restored from a local-only package feed,
never your global cache or installed skills. Python packages are test-only dependencies, not
dependencies of the .NET tool.

```powershell
dotnet build -c Release
python -m venv artifacts\terminal-venv
artifacts\terminal-venv\Scripts\python.exe -m pip install -r tests\terminal\requirements.txt
artifacts\terminal-venv\Scripts\python.exe tests\terminal\verify_picker.py `
  --tool src\DotnetPackageSkills\bin\Release\net10.0\dotnet-package-skills.exe `
  --artifacts artifacts\terminal-results
```

Rendered frames and raw terminal output are saved under the supplied artifacts directory.
The suite checks descriptions, action/focus colors, no-color markers, variable-height pagination,
scrolling, resize, confirm/cancel, dry runs, ownership preservation, and the unchanged JSON contract.
Fixtures are removed after each case; logs remain available for diagnosing failures.

### Naming unit tests

Name tests as a sentence describing the behaviour, not the method under test:

```csharp
[Fact]
public void Install_skips_a_later_skill_when_destination_names_collide()
```

New behaviour needs a test. Bug fixes need a test that fails without the fix — the `.slnx`
preference bug shipped with one, and that is why it stayed fixed.

## Style

`TreatWarningsAsErrors` is on; builds must be warning-clean. Beyond that, match the surrounding
code. Comments explain *why*, not what — if a comment restates the code, delete it.

## Compatibility

- The tool targets `net8.0` and `net10.0`. Don't drop `net8.0` without a discussion; it is the LTS
  a lot of teams are still on.
- `dotnet list package --format json` requires SDK 7.0.200+. That is the floor for what the tool
  can inspect, and the error message says so when it isn't met.
- **`dotnet list package` restores implicitly**, so `--no-restore` has to be forwarded to it. Left
  off, the SDK restores anyway, the command succeeds, and the user's `--no-restore` becomes a
  silent no-op — the failure mode is invisible, which is why `PackageListerTests` asserts the flag
  is passed through.
- Output of `dotnet nuget locals` has changed shape across SDK versions. Parsing keys off the
  `global-packages:` label rather than line position — keep it that way.

## Pull requests

- One change per PR.
- `dotnet build` and `dotnet test` pass.
- README updated if you changed the CLI surface.
- Say what you tested it against. "Ran `install` on a solution with 40 packages, two of which ship
  skills" is worth more than a description of the diff.
