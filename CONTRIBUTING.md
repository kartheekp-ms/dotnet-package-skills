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

Install your build as a global tool when you want to run the installed `dotnet-package-skills`
command itself:

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
├── Cli/OutputWriter.cs     Human-readable reports
├── Cli/SkillPicker.cs      The --interactive picker, paged so one screen is one page
├── Cli/ITerminal.cs        Console access behind an interface, so the picker can be tested
├── Cli/InteractiveSkills.cs  Picker-only metadata and selection mapping
├── Infrastructure/         Process execution and the dotnet CLI wrapper
├── NuGet/                  Target detection, package listing, cache path resolution
└── Skills/                 Discovery, copying, version-change removal, the install manifest

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
records, for each package ID, the one installed version and the skill folder names it owns;
`install` (when a package changes version) and `uninstall` act only on those names. Users keep
their own hand-written skills in the same folder, and deleting one of those would be unforgivable.

**An unreadable manifest stops every operation that needs ownership.** Never treat a malformed or
unreadable manifest as empty. Empty means the existing folders are user-owned; corrupt means their
ownership is unknown. `install` and `uninstall` must fail before changing anything and preserve the
file so the user can repair or restore it. `list` may still run because it does not read ownership
or write anything.
An existing manifest must have a whole-number format `version` that the tool supports and a
`packages` object. Package IDs must be valid by NuGet's own rule (`PackageCoordinate.IsValidId`,
which allows letters outside ASCII), every package needs a non-empty version, JSON properties must
be unique ignoring case, and case-insensitive destination claims must be unique. A newer format
version asks the user to update the tool. A pre-release manifest, which has an `installed` array,
is refused rather than converted. `SetSkills` refuses an ID that the reader would refuse, so the
tool never writes a manifest that locks the destination.
For v1, use ordinary manifest files and update them in place. The tool does not create symbolic
links, and linked or redirected manifests are outside the v1 safety guarantees. Do not replace an
existing manifest with a new inode merely to handle links: that can change Unix ownership or ACLs.

**The manifest is a public contract.** Reports have no machine-readable form, so the manifest is
what scripts read, and teams commit it. Its shape follows `dotnet-tools.json`: a format `version`
and a `packages` object keyed by lowercase package ID. A change that an older tool would misread
needs a new format `version`, so older tools refuse the file instead of guessing. Write it the same
way on every platform: UTF-8 without a BOM, LF line endings with a final newline, packages and
skills in ordinal order, and values escaped identically on `net8.0` and `net10.0`
(`JsonWriterOptions.NewLine` doesn't exist on .NET 8, so the writer's CRLF is replaced after
serializing). A platform-dependent byte is churn in someone's pull request.

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
rule so users can make an informed selection. Reports and the manifest never include descriptions.

**Skill names from packages are untrusted input.** They become path segments in the user's repo.
`SkillDiscovery.IsSafeSkillName` is the shared discovery/manifest gate; keep it strict. Reject names
ending in dots or spaces on every platform, since Windows can normalize them to another folder or
the destination itself. Before mutation, resolve all affected skill paths as direct children of the
destination; removing a skill must not walk up and delete its parents.

**`install` removes a skill only when its package changes version.** A noninteractive install
offers the resolved packages it found in the cache. A tracked skill goes only if its package is
offered at a different normalized version, the new version doesn't ship it, and no ownership
conflict protects it. When a conflict does protect it, `install` stops instead: removing it would
hand its name to the other package, and relabeling it would record the old version's copy under
the new version, where no later run would remove it. A package that left the project is never a
reason to delete, because a reference can vanish for a moment: `install` reports those skills as
unreferenced, and `uninstall --stale` removes them when asked. A version missing from the cache
never causes a removal, and a target install stops before any writes, including previews, when a
resolved package is missing. An incomplete cache must never look like permission to delete skills.

**`install -i` only adds.** The checklist lists only skills that would install cleanly and aren't
tracked, nothing starts checked, and the installer gets an empty offered-packages map, so nothing
is refreshed or removed. Every check that could stop the run happens before the checklist opens:
two versions of a package; with a target, a missing package or a stale skill; with `--package`, a
named package tracked at another version. Adding beside a stale skill would leave the manifest
disagreeing with the project, and adding beside another version would give a package two versions.

**`uninstall --stale` reads references, not packages.** It needs a solution or project, and it
compares the manifest with the target's direct package references from `dotnet list package`. It
never asks where the NuGet cache is, so a missing or partial cache can't change what counts as
stale. A skill is stale when no referenced package has its ID and installed version, which also
keeps it working when a target resolves two versions. `--stale` can't be combined with `--package`,
and `uninstall` accepts `--target` and `--no-restore` only with `--stale`.

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
to erase without ANSI. And the widest summary, with every row ticked, is measured rather than the
current one, so counts growing as you select never reflow the frame. Chrome that would do nothing
is dropped: no counter on a single page, no movement or select-all keys for a single skill. The
note under the title is optional chrome too: when the window can't fit it, the layout drops the
note rather than refusing to open.

**Keyboard hints follow Aspire's checklist style.** The primary hint is
`(Press <space> to select, <enter> to accept)`, below the list. Use the same angle-bracket key
notation for paging, select-all, clear-all, cancel, and description scrolling, beginning each
keyboard-help line with `Press`. Wrap help rather than clipping away the keys. Only advertise
paging and scrolling when they are useful.

**Focus and checked state are the only cues.** The focused skill's text and all wrapped
description lines are blue. A checked item has a blue uppercase `X` in both pickers; names and
brackets do not change color because of selection. Neither picker marks what a tick does: each
does one thing, so the title and the summary say it, and the uninstall summary also says how many
will go. No-color terminals show `>` and `[X]` with no extra marker and no legend; respect
`NO_COLOR`. A dedicated status column would take space away from descriptions.

**A tick means install in one picker and remove in the other.** Neither starts with anything
ticked, so pressing enter without touching anything changes nothing in both. `PickerMode` carries
the difference, which shows only in the summary; the caller supplies the title. The uninstall list
comes from the manifest, so a skill someone wrote by hand is never offered for deletion.

**The picker owns the terminal, so it has to hand it back.** `Choose` hides the cursor and takes
Ctrl+C as input, and restores both in a `finally`. Ctrl+C is why: left to the runtime it ends the
process mid-frame, so the restore never runs and the user is left typing into a terminal with no
cursor. Taken as a key it cancels through the same path as `esc`. Note the modifier is tested
before the switch, because a bare `c` clears the selection.

**An interactive choice applies only to the ownership it was made against.** Ownership snapshots
are rechecked before applying an interactive choice; concurrent ownership changes invalidate it
rather than changing which package's files are affected.
Hold the destination lock from ownership loading through the final manifest write. Both
installation and uninstallation participate, so another tool invocation cannot change ownership
between the check and mutation. Canonicalize destination aliases before choosing the lock.

**Resizing invalidates an in-progress frame.** Read width and height together, restart a redraw
if its viewport changes, and clear cells in place rather than scrolling blank lines. Otherwise
old picker copies accumulate in terminal history and can wrap incorrectly when the host resizes.
Preserve prior scrollback, focus, and selections; never swallow unrelated rendering failures.
The picker owns an alternate screen for its entire lifetime. Clearing just the current viewport
cannot erase old rows that the host has already reflowed into normal history. Restore the original
screen and output mode on every managed exit, then write the final report on the normal screen.
Clear and home the alternate viewport before the first frame, just as after a resize. Entering the
alternate screen can preserve the shell cursor position; reserving space with blank lines then
leaves a gap above a compact checklist. Never clear the normal screen to fix that gap.

Every render also parks the cursor directly below the last line it drew, rather than at the bottom
of the layout's maximum height. `SkillPickerTests` pins that placement for short pages; managed exits
restore the original shell cursor independently when they leave the alternate screen.

**Picker chrome is ASCII; author text is not restricted to English.** Keep control hints and
markers ASCII so legacy console encodings do not lose them. Display Unicode descriptions without
splitting text elements, measuring terminal cells rather than UTF-16 code units. Strip unsafe
terminal control sequences from author-supplied text. All color goes through `ITerminal`, and the
picker uses BOM-less UTF-8 while prompting. Restore the original encoding and terminal styling
when it exits or fails, so ordinary command output retains its existing behavior.

**Human-readable reports must not execute metadata as terminal commands.** Sanitize each untrusted
display field with `TerminalText.Sanitize`, including package/version metadata, paths, skipped
reasons, and operational errors. Framework parser diagnostics and suggestions use a separate output
path and must be sanitized too, including split writes. Keep multiline error guidance readable.
Never sanitize arguments before validation or persist sanitized display values; canonical
identities must remain intact.

**Reports are for people; there is no JSON report.** The manifest is the machine-readable record,
and exit codes carry success or failure. Don't bring back a `--json` report without revisiting
that decision. Because `--package` accepts several values, the parser hands it any unknown option
that follows, such as a `--json` left in an old script; its validator reports a value starting
with `-` as an unrecognized argument rather than as a malformed package.

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
mode. A skipped conflicting path is protected from version-change removal as well as copying.
Same-package version refreshes remain allowed. A protected skill is never relabeled to a version
that doesn't ship it; that case stops the install, as described above.
Keep all discovery candidates internally until install-time ownership is known. Prefer the
current owner's candidate; `list` remains a destination-independent discovery report.
Package authors avoid collisions by prefixing skill folders with their lowercased package ID, but
the tool does not enforce that naming convention.
For v1, retain logical case-insensitive matching without reconciling distinct physical case variants.
Package authors should keep folder casing stable. Case-only renames and mixed-case physical entries
on case-sensitive filesystems are outside the v1 ownership guarantees; do not promise safe migration
or add special reconciliation logic without revisiting that scope.

**Package filters must not broaden destructive operations.** An explicitly blank filter is an
error; only an absent `--package` means all packages. Interactive and noninteractive uninstall
use the same normalized version matcher.

**One version per package, or no install.** The manifest records one version per package. When
the resolved packages, or the `--package` coordinates, include two normalized versions of one ID,
every install mode stops before any change and asks for the versions to be aligned; repositories
are expected to use NuGet Central Package Management. `PackageLister.Parse` keeps distinct
`(id, version)` pairs so the check can see them, and `list` still shows both.

**Errors should read as guidance.** Throw `PackageSkillsException` with a message that tells the
user what to do next. `Program.cs` prints it without a stack trace. If a message would leave
someone stuck, it needs more words. A command in a message has to work when pasted as printed:
build it with `SkillInstallService.UninstallCommand`, which repeats the run's `--target` and
non-default `--destination`.

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
npm ci --prefix tests\terminal --ignore-scripts --no-audit --no-fund
artifacts\terminal-venv\Scripts\python.exe tests\terminal\verify_picker.py `
  --tool src\DotnetPackageSkills\bin\Release\net10.0\dotnet-package-skills.exe `
  --artifacts artifacts\terminal-results
```

Rendered frames and raw terminal output are saved under the supplied artifacts directory.
The suite checks descriptions, focus and checked colors, no-color rendering, variable-height pagination,
scrolling, resize, confirm/cancel, dry runs, ownership preservation, the add-only install
checklist and its pre-flight stops, `uninstall --stale`, the two-versions stop, the rejected
`--json` option, and the manifest's bytes.
The duplicate-frame regressions also feed the real terminal output through an xterm emulator
that models normal-buffer text reflow and alternate screens; a clipped-cell mock alone misses
the host behavior that originally left stale headings in scrollback.
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
