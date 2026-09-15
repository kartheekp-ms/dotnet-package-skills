"""Windows ConPTY regressions against a built or locally installed tool.

python verify_picker.py --tool <dotnet-package-skills.exe> --artifacts <directory>
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import subprocess
import sys
import tempfile
import threading
import time
import unicodedata
import unittest
from xml.sax.saxutils import escape
from zipfile import ZipFile

if sys.platform != "win32":
    raise SystemExit("These real-console tests require Windows ConPTY.")

import pyte
from winpty import Backend, PtyProcess


OPTIONS = None
ENTER, ESC, SPACE = "\r", "\x1b", " "
UP, DOWN, LEFT, RIGHT = "\x1b[A", "\x1b[B", "\x1b[D", "\x1b[C"
HOME, END = "\x1b[H", "\x1b[F"
PAGE_UP, PAGE_DOWN = "\x1b[5~", "\x1b[6~"
CTRL_UP, CTRL_DOWN, CTRL_C = "\x1b[1;5A", "\x1b[1;5B", "\x03"
ASPIRE_HINT = "(Press <space> to select, <enter> to accept)"


class Terminal:
    def __init__(self, command, directory, environment, log, rows=24, columns=100):
        self.process = PtyProcess.spawn(
            command, cwd=str(directory), env=environment,
            dimensions=(rows, columns), backend=Backend.ConPTY,
        )
        self.screen = pyte.Screen(columns, rows)
        self.stream = pyte.Stream(self.screen)
        self.log = log
        self.output = queue.Queue()
        self.raw = []
        self.frames = []
        self.ended = False
        self.closing = threading.Event()
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()

    def _read(self):
        try:
            while True:
                data = self.process.read(65536)
                if not data:
                    break
                self.output.put(data)
        except EOFError:
            pass
        except OSError as error:
            if not self.closing.is_set():
                self.output.put(error)
        except Exception as error:
            self.output.put(error)
        finally:
            self.output.put(None)

    def pump(self, timeout=0.03):
        try:
            data = self.output.get(timeout=timeout)
        except queue.Empty:
            return False
        if data is None:
            self.ended = True
        elif isinstance(data, Exception):
            raise data
        else:
            self.raw.append(data)
            self.stream.feed(data)
        return True

    @property
    def text(self):
        return "\n".join(line.rstrip() for line in self.screen.display).rstrip()

    @property
    def compact(self):
        return re.sub(r"\s+", " ", self.text)

    @property
    def focused(self):
        return next((line for line in self.screen.display if re.match(r"\s*>", line)), "")

    @property
    def page_count(self):
        match = re.search(r"page\s+\d+\s+of\s+(\d+)", self.compact, re.IGNORECASE)
        return int(match[1]) if match else 1

    def wait_for(self, predicate, label, timeout=15):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            self.pump()
            if predicate():
                while self.pump(timeout=0.08):
                    pass
                if predicate():
                    self.frames.append(f"--- {label} ---\n{self.text}")
                    return
            if self.ended:
                break
        self.frames.append(f"--- failed: {label} ---\n{self.text}")
        raise AssertionError(f"Terminal did not reach {label}.\n{self.text}")

    def ready(self):
        self.wait_for(
            lambda: ASPIRE_HINT in self.compact and re.search(r"\[[ x]\]", self.text),
            "initial picker",
        )
        return self

    def press(self, keys, predicate=None, label="keypress"):
        self.process.write(keys)
        if predicate is not None:
            self.wait_for(predicate, label)

    def focus(self, name, ordinal):
        self.press(HOME)
        self.press(DOWN * ordinal, lambda: name in self.focused, f"focus {name}")

    def colors(self, text):
        for y, line in enumerate(self.screen.display):
            start = line.find(text)
            if start >= 0:
                return [self.screen.buffer[y][x].fg for x in range(start, start + len(text))]
        raise AssertionError(f"{text!r} is not visible.\n{self.text}")

    def resize(self, rows, columns):
        self.screen.resize(lines=rows, columns=columns)
        self.process.setwinsize(rows, columns)

    def finish(self, keys=ENTER):
        self.process.write(keys)
        self.wait_for(lambda: self.ended, "process exit")
        # Close before querying status: PtyProcess.wait() marks the object closed without
        # releasing its reader sockets in pywinpty 3.0.5.
        self.process.close()
        return self.process.exitstatus

    def close(self):
        self.closing.set()
        self.process.close(force=True)
        self.reader.join(timeout=3)
        while self.pump(timeout=0.01):
            pass
        self.log.parent.mkdir(parents=True, exist_ok=True)
        self.log.with_suffix(".ansi.txt").write_text("".join(self.raw), encoding="utf-8")
        self.log.with_suffix(".screen.txt").write_text("\n\n".join(self.frames), encoding="utf-8")


def snapshot(directory):
    return {
        str(path.relative_to(directory)): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in directory.rglob("*") if path.is_file()
    }


class PickerRegression(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix="picker-", dir=OPTIONS.artifacts)
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name)
        self.cache = self.root / "package cache"
        self.destination = self.root / "installed skills"
        self.environment = dict(os.environ)
        self.environment.pop("NO_COLOR", None)
        self.environment["TERM"] = "xterm-256color"
        self.environment["NUGET_PACKAGES"] = str(self.cache)
        self.names = []
        for package, version, prefix, count in (
            ("Demo.Alpha", "1.0.0", "alpha", 15),
            ("Demo.Beta", "2.0.0", "beta", 10),
        ):
            for number in range(1, count + 1):
                name = f"{prefix}-{number:02}"
                self.names.append(name)
                directory = self.cache / package.lower() / version / "skills" / name
                directory.mkdir(parents=True)
                description = f"Guidance for {name}: configure services and verify expected behavior."
                if name == "alpha-01":
                    description = "ALPHA-FIRST: configure test doubles and check their calls."
                if name == "alpha-02":
                    description += " Keep changes predictable."
                if name == "alpha-05":
                    description = (
                        "LONG-DESCRIPTION-START "
                        + " ".join(
                            f"Step {step:02}: every line of this description must remain readable."
                            for step in range(36)
                        )
                        + "LONG-DESCRIPTION-END"
                    )
                contents = f"---\nname: {name}\ndescription: {json.dumps(description)}\n---\n# Body\n"
                if name == "alpha-03":
                    contents = "# No frontmatter\n"
                elif name == "alpha-04":
                    contents = "---\ndescription: [broken\n---\n"
                (directory / "SKILL.md").write_text(contents, encoding="utf-8")
        handwritten = self.destination / "team-owned"
        handwritten.mkdir(parents=True)
        (handwritten / "SKILL.md").write_text("Do not change our own skill.\n", encoding="utf-8")
        self.manifest = self.destination / ".dotnet-package-skills.json"
        self.target = None
        self.terminal_count = 0

    def command(self, verb, *extra, packages=None):
        args = [str(OPTIONS.tool), verb, "--destination", str(self.destination)]
        if verb != "uninstall":
            args += ["--global-packages", str(self.cache)]
            if self.target is not None:
                args += ["--target", str(self.target), "--no-restore"]
            else:
                for package in packages or ["Demo.Alpha@1.0.0", "Demo.Beta@2.0.0"]:
                    args += ["--package", package]
        return args + list(extra)

    def cli(self, verb, *extra, packages=None, expected=0):
        result = subprocess.run(
            self.command(verb, *extra, packages=packages),
            cwd=self.root, env=self.environment, capture_output=True,
            encoding="utf-8", timeout=30,
        )
        self.assertEqual(expected, result.returncode, result.stdout + result.stderr)
        return result

    def terminal(self, verb="install", *extra, packages=None, rows=24, columns=100, no_color=False,
                 powershell=False):
        environment = dict(self.environment)
        if no_color:
            environment["NO_COLOR"] = "1"
        self.terminal_count += 1
        command = self.command(verb, "-i", *extra, packages=packages)
        if powershell:
            invocation = "& " + " ".join("'" + argument.replace("'", "''") + "'" for argument in command)
            command = [
                "powershell.exe", "-NoLogo", "-NoProfile", "-Command",
                "1..90 | ForEach-Object { Write-Output \"earlier console output $_\" }; "
                + invocation + "; exit $LASTEXITCODE",
            ]
        terminal = Terminal(
            command,
            self.root, environment,
            OPTIONS.artifacts / "logs" / f"{self._testMethodName}-{self.terminal_count}",
            rows, columns,
        )
        self.addCleanup(terminal.close)
        return terminal

    def installed(self):
        if not self.manifest.exists():
            return set()
        manifest = json.loads(self.manifest.read_text(encoding="utf-8-sig"))
        return {name for package in manifest["installed"] for name in package["skills"]}

    def assert_frame(self, terminal):
        self.assertIn("Which skills should", terminal.compact)
        self.assertIn(ASPIRE_HINT, terminal.compact)
        for hint in re.findall(r"\([^()]*<[^()]*\)", terminal.compact):
            self.assertTrue(hint.startswith("(Press <"), hint)
        self.assertNotIn("will install", terminal.text.lower())
        self.assertNotIn("will remove", terminal.text.lower())
        self.assertNotIn("(Demo.Alpha 1.0.0)", terminal.text)
        self.assertNotIn("(Demo.Beta 2.0.0)", terminal.text)
        self.assertIn("selected", terminal.text)

    def test_plain_commands_and_json_contract_do_not_gain_descriptions(self):
        before = snapshot(self.destination)
        result = json.loads(self.cli("list", "--json").stdout)
        self.assertEqual(25, len(result["skills"]))
        expected_keys = {"packageId", "packageVersion", "skillName", "sourcePath", "relativePath"}
        for skill in result["skills"]:
            self.assertEqual(expected_keys, set(skill))
        self.assertEqual(before, snapshot(self.destination))
        self.cli("install", "--dry-run")
        self.assertEqual(before, snapshot(self.destination))
        self.cli("install")
        self.assertEqual(set(self.names), self.installed())
        installed = snapshot(self.destination)
        self.cli("install")
        self.assertEqual(installed, snapshot(self.destination))
        self.cli("uninstall", "--dry-run")
        self.assertEqual(installed, snapshot(self.destination))
        removed = json.loads(self.cli("uninstall", "--json").stdout)["removed"]
        self.assertEqual(25, len(removed))
        for skill in removed:
            self.assertEqual({"packageId", "packageVersion", "skillName", "relativePath"}, set(skill))
        self.assertEqual(before, snapshot(self.destination))

    def test_restored_solution_supports_descriptions_and_project_scoped_pruning(self):
        feed = self.root / "local feed"
        feed.mkdir()
        for package, version in (("Demo.Alpha", "1.0.0"), ("Demo.Beta", "2.0.0")):
            source = self.cache / package.lower() / version
            with ZipFile(feed / f"{package}.{version}.nupkg", "w") as archive:
                archive.writestr(
                    f"{package}.nuspec",
                    f'<?xml version="1.0"?><package><metadata><id>{package}</id>'
                    f"<version>{version}</version><authors>Regression fixture</authors>"
                    "<description>Local terminal test fixture.</description></metadata></package>",
                )
                for path in source.rglob("*"):
                    if path.is_file():
                        archive.write(path, path.relative_to(source).as_posix())
        project = self.root / "My App"
        project.mkdir()
        project_file = project / "App.csproj"
        beta_reference = '<PackageReference Include="Demo.Beta" Version="2.0.0" />'
        project_xml = (
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            "<TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>"
            '<PackageReference Include="Demo.Alpha" Version="1.0.0" />'
            f"{beta_reference}</ItemGroup></Project>"
        )
        project_file.write_text(project_xml, encoding="utf-8")
        self.target = project / "App.slnx"
        self.target.write_text('<Solution><Project Path="App.csproj" /></Solution>', encoding="utf-8")
        config = project / "NuGet.Config"
        config.write_text(
            '<?xml version="1.0"?><configuration><packageSources><clear />'
            f'<add key="local" value="{escape(str(feed))}" /></packageSources></configuration>',
            encoding="utf-8",
        )
        self.cache = self.root / "restored cache"
        self.environment["NUGET_PACKAGES"] = str(self.cache)

        def restore():
            result = subprocess.run(
                ["dotnet", "restore", str(self.target), "--configfile", str(config),
                 "--nologo", "--verbosity", "quiet"],
                cwd=project, env=self.environment, capture_output=True, encoding="utf-8", timeout=60,
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

        restore()
        listed = json.loads(self.cli("list", "--json").stdout)
        self.assertEqual(25, len(listed["skills"]))
        self.assertEqual(str(self.target), listed["target"])
        terminal = self.terminal().ready()
        self.assertIn("App.slnx", terminal.text)
        self.assertIn("ALPHA-FIRST", terminal.text)
        terminal.press(SPACE)
        self.assertEqual(0, terminal.finish())
        self.assertEqual({"alpha-01"}, self.installed())
        self.cli("install")
        self.assertEqual(set(self.names), self.installed())
        project_file.write_text(project_xml.replace(beta_reference, ""), encoding="utf-8")
        restore()
        removed = json.loads(self.cli("install", "--dry-run", "--json").stdout)["removed"]
        self.assertEqual({f"beta-{number:02}" for number in range(1, 11)}, {s["skillName"] for s in removed})
        self.assertEqual(set(self.names), self.installed())
        self.cli("install")
        self.assertEqual({f"alpha-{number:02}" for number in range(1, 16)}, self.installed())
        self.assertTrue((self.destination / "team-owned" / "SKILL.md").exists())

    def test_aspire_hint_descriptions_pagination_and_action_colors(self):
        terminal = self.terminal().ready()
        self.assert_frame(terminal)
        self.assertIn("ALPHA-FIRST", terminal.text)
        rows = terminal.screen.display
        first_line = next(index for index, line in enumerate(rows) if "Guidance for alpha-02" in line)
        description_column = rows[first_line].index("Guidance")
        self.assertEqual(
            6,
            len(rows[first_line + 1]) - len(rows[first_line + 1].lstrip()),
        )
        self.assertEqual(
            "Guidance for alpha-02: configure services and verify expected behavior. Keep changes predictable.",
            rows[first_line][description_column:].strip()
            + " " + rows[first_line + 1][6:].strip(),
        )
        pages = re.search(r"page\s+1\s+of\s+(\d+)", terminal.compact, re.IGNORECASE)
        self.assertIsNotNone(pages, terminal.text)
        self.assertGreaterEqual(int(pages[1]), 3)
        self.assertEqual({"brightblue"}, set(terminal.colors(">")))
        terminal.press(SPACE, lambda: "[x]" in terminal.focused, "pending install")
        self.assertEqual({"brightgreen"}, set(terminal.colors("alpha-01")))
        self.assertEqual({"brightblue"}, set(terminal.colors(">")))
        self.assertNotIn("brightgreen", terminal.colors("ALPHA-FIRST"))
        terminal.press(SPACE, lambda: "[ ]" in terminal.focused, "undo install")
        self.assertNotIn("brightgreen", terminal.colors("alpha-01"))
        self.assertEqual(0, terminal.finish(ESC))
        self.assertFalse(self.manifest.exists())

    def test_each_name_is_followed_directly_by_the_description_without_package_metadata(self):
        self.cache = self.root / "compact cache"
        self.environment["NUGET_PACKAGES"] = str(self.cache)
        names = ["demo.alpha-longer-name", "demo.alpha-short"]
        for name in names:
            directory = self.cache / "demo.alpha" / "1.0.0" / "skills" / name
            directory.mkdir(parents=True)
            (directory / "SKILL.md").write_text(
                f"---\ndescription: Guidance for {name}.\n---\n", encoding="utf-8")
        self.cli("install", packages=["Demo.Alpha@1.0.0"])
        before = snapshot(self.destination)

        for verb in ("install", "uninstall"):
            for no_color in (False, True):
                with self.subTest(verb=verb, no_color=no_color):
                    terminal = self.terminal(
                        verb, packages=["Demo.Alpha@1.0.0"], no_color=no_color,
                    ).ready()
                    for name in names:
                        self.assertIn(f"{name} - Guidance for {name}.", terminal.text)
                    self.assertNotIn("(Demo.Alpha 1.0.0)", terminal.text)
                    self.assertIn(ASPIRE_HINT, terminal.compact)
                    self.assertEqual(0, terminal.finish(ESC))
                    self.assertEqual(before, snapshot(self.destination))

    def test_select_and_clear_apply_across_all_pages(self):
        terminal = self.terminal().ready()
        terminal.press("a", lambda: "25 of 25" in terminal.compact, "select all pages")
        terminal.press(END, lambda: "beta-10" in terminal.focused, "last skill")
        self.assertIn("[x]", terminal.focused)
        terminal.press("c", lambda: "0 of 25" in terminal.compact, "clear all pages")
        terminal.press(HOME, lambda: "alpha-01" in terminal.focused, "first skill")
        self.assertIn("[ ]", terminal.focused)
        self.assertEqual(0, terminal.finish())
        self.assertEqual(set(), self.installed())

    def test_page_keys_home_end_and_wraparound_keep_a_complete_frame(self):
        terminal = self.terminal().ready()
        terminal.press(END, lambda: "beta-10" in terminal.focused, "last page")
        self.assert_frame(terminal)
        self.assertNotIn("alpha-01", terminal.text)
        terminal.press(DOWN, lambda: "alpha-01" in terminal.focused, "wrap to first")
        terminal.press(UP, lambda: "beta-10" in terminal.focused, "wrap to last")
        terminal.press(HOME, lambda: "alpha-01" in terminal.focused, "home")
        for forward, backward in ((RIGHT, LEFT), (PAGE_DOWN, PAGE_UP)):
            terminal.press(forward, lambda: "page 2 of" in terminal.compact.lower(), "next page")
            self.assert_frame(terminal)
            terminal.press(backward, lambda: "page 1 of" in terminal.compact.lower(), "previous page")
            self.assert_frame(terminal)
        self.assertEqual(0, terminal.finish(ESC))

    def test_confirmation_only_installs_checked_skills(self):
        terminal = self.terminal().ready()
        terminal.press(SPACE)
        terminal.press(DOWN, lambda: "alpha-02" in terminal.focused, "second skill")
        terminal.press(SPACE)
        self.assertEqual(0, terminal.finish())
        self.assertEqual({"alpha-01", "alpha-02"}, self.installed())
        self.assertTrue((self.destination / "team-owned" / "SKILL.md").exists())

    def test_reopening_preselects_installed_items_and_shows_removal_in_red(self):
        self.cli("install", packages=["Demo.Alpha@1.0.0"])
        before = snapshot(self.destination)
        terminal = self.terminal().ready()
        self.assertIn("[x]", terminal.focused)
        self.assertNotIn("brightgreen", terminal.colors("alpha-01"))
        terminal.press(SPACE, lambda: "[ ]" in terminal.focused, "pending removal")
        self.assertEqual({"brightred"}, set(terminal.colors("alpha-01")))
        terminal.press(SPACE, lambda: "[x]" in terminal.focused, "keep installed")
        self.assertNotIn("brightred", terminal.colors("alpha-01"))
        self.assertEqual(0, terminal.finish())
        self.assertEqual(before, snapshot(self.destination))

    def test_package_filtered_install_cannot_remove_hidden_installed_skills(self):
        self.cli("install")
        terminal = self.terminal(packages=["Demo.Alpha@1.0.0"]).ready()
        terminal.press(SPACE, lambda: "[ ]" in terminal.focused, "deselect filtered skill")
        self.assertEqual(0, terminal.finish())
        self.assertEqual(set(self.names) - {"alpha-01"}, self.installed())

    def test_uninstall_reads_installed_descriptions_without_the_package_cache(self):
        self.cli("install", packages=["Demo.Beta@2.0.0"])
        (self.destination / "beta-01" / "SKILL.md").write_text(
            "---\ndescription: INSTALLED-COPY-ONLY\n---\n", encoding="utf-8")
        self.cache.rename(self.root / "cache moved aside")
        terminal = self.terminal("uninstall").ready()
        self.assertIn("INSTALLED-COPY-ONLY", terminal.text)
        self.assertNotIn("team-owned", terminal.text)
        self.assertNotIn("alpha-", terminal.text)
        self.assertIn("0 of 10", terminal.compact)
        terminal.press(SPACE, lambda: "[x]" in terminal.focused, "select removal")
        self.assertEqual({"brightred"}, set(terminal.colors("beta-01")))
        self.assertEqual(0, terminal.finish())
        self.assertEqual({f"beta-{number:02}" for number in range(2, 11)}, self.installed())

    def test_missing_and_invalid_descriptions_are_visible_and_selectable(self):
        terminal = self.terminal().ready()
        terminal.focus("alpha-03", 2)
        self.assertIn("No description provided.", terminal.compact)
        terminal.press(SPACE)
        terminal.focus("alpha-04", 3)
        self.assertIn("Description unavailable", terminal.compact)
        terminal.press(SPACE)
        self.assertEqual(0, terminal.finish())
        self.assertEqual({"alpha-03", "alpha-04"}, self.installed())

    def test_unicode_descriptions_are_readable_and_cannot_send_terminal_controls(self):
        description = "Café, 測試, 🧪, Cafe\u0301. \x1b[2JUNICODE-END"
        path = self.cache / "demo.alpha" / "1.0.0" / "skills" / "alpha-01" / "SKILL.md"
        path.write_text(
            f"---\ndescription: {json.dumps(description)}\n---\n",
            encoding="utf-8",
        )
        before = snapshot(self.destination)
        terminal = self.terminal(columns=120).ready()
        self.assert_frame(terminal)
        self.assertIn("Café", unicodedata.normalize("NFC", terminal.text))
        self.assertIn("測試", terminal.text)
        self.assertIn("🧪", terminal.text)
        self.assertIn("UNICODE-END", terminal.text)
        self.assertEqual(0, terminal.finish(ESC))
        self.assertEqual(before, snapshot(self.destination))

    def test_oversized_description_can_be_scrolled_to_its_end(self):
        terminal = self.terminal(rows=18, columns=80).ready()
        terminal.focus("alpha-05", 4)
        self.assertIn("LONG-DESCRIPTION-START", terminal.compact)
        self.assertIn("scroll", terminal.compact.lower())
        for scroll in range(250):
            if "LONG-DESCRIPTION-END" in terminal.compact:
                break
            previous = terminal.text
            terminal.press(
                CTRL_DOWN,
                lambda: terminal.text != previous,
                f"description scroll {scroll}",
            )
        self.assertIn("LONG-DESCRIPTION-END", terminal.compact)
        self.assertIn("0 of 25", terminal.compact)
        self.assertEqual(0, terminal.finish(ESC))

    def test_resize_preserves_focus_selection_and_keyboard_hints(self):
        terminal = self.terminal(rows=32, columns=120).ready()
        terminal.press(SPACE)
        terminal.focus("alpha-02", 1)
        initial_pages = terminal.page_count
        terminal.resize(18, 46)
        terminal.wait_for(
            lambda: terminal.page_count > initial_pages
            and "alpha-02" in terminal.focused and ASPIRE_HINT in terminal.compact,
            "narrow resize without keyboard input",
        )
        self.assert_frame(terminal)
        terminal.press(DOWN, lambda: "alpha-03" in terminal.focused, "narrow resized frame")
        self.assert_frame(terminal)
        self.assertIn("1 of 25", terminal.compact)
        narrow_pages = terminal.page_count
        terminal.resize(40, 140)
        terminal.wait_for(
            lambda: terminal.page_count < narrow_pages
            and "alpha-03" in terminal.focused and ASPIRE_HINT in terminal.compact,
            "wide resize without keyboard input",
        )
        self.assert_frame(terminal)
        terminal.press(HOME, lambda: "alpha-01" in terminal.focused, "wide resized frame")
        self.assert_frame(terminal)
        self.assertIn("[x]", terminal.focused)
        self.assertEqual(0, terminal.finish())
        self.assertEqual({"alpha-01"}, self.installed())

    def test_rapid_live_resizes_do_not_leave_old_picker_frames_in_scrollback(self):
        before = snapshot(self.destination)
        for powershell in (False, True):
            with self.subTest(powershell=powershell):
                terminal = self.terminal(rows=70, columns=210, powershell=powershell)
                terminal.screen = pyte.HistoryScreen(210, 70, history=2000)
                terminal.stream = pyte.Stream(terminal.screen)
                terminal.ready()
                terminal.press(SPACE, lambda: "1 of 25" in terminal.compact, "selected before resizing")
                for cycle in range(3):
                    for columns, rows in ((170, 60), (130, 45), (80, 24), (110, 40), (210, 70)):
                        terminal.resize(rows, columns)
                        time.sleep(0.006)
                        terminal.pump(timeout=0.01)
                    terminal.resize(30, 100)
                    terminal.wait_for(
                        lambda: terminal.page_count > 1 and "alpha-01" in terminal.focused
                        and "1 of 25" in terminal.compact and ASPIRE_HINT in terminal.compact,
                        f"settled rapid resize {cycle}",
                    )
                    self.assert_frame(terminal)
                    history = "\n".join(
                        "".join(cell.data for _, cell in sorted(row.items()))
                        for row in terminal.screen.history.top
                    )
                    self.assertNotIn("Which skills should be installed?", history)
                    if powershell:
                        self.assertIn("earlier console output", history)
                self.assertEqual(0, terminal.finish(ESC))
                self.assertEqual(before, snapshot(self.destination))

    def test_no_color_mode_exposes_action_markers_without_color(self):
        terminal = self.terminal(no_color=True).ready()
        terminal.press(SPACE, lambda: "[x]" in terminal.focused, "no-color installation")
        self.assertIn("+", terminal.focused)
        self.assertNotIn("brightgreen", terminal.colors("alpha-01"))
        self.assertNotIn("brightblue", terminal.colors(">"))
        self.assertEqual(0, terminal.finish())
        terminal = self.terminal("uninstall", no_color=True).ready()
        terminal.press(SPACE, lambda: "[x]" in terminal.focused, "no-color removal")
        self.assertIn("-", terminal.focused.split("alpha-01")[0])
        self.assertEqual(0, terminal.finish())
        self.assertEqual(set(), self.installed())

    def test_cancel_keys_preserve_files_and_restore_the_cursor(self):
        self.cli("install")
        before = snapshot(self.destination)
        for key in (ESC, "q", CTRL_C):
            terminal = self.terminal().ready()
            terminal.press("c", lambda: "0 of 25" in terminal.compact, "pending removal of all")
            self.assertEqual(0, terminal.finish(key))
            self.assertEqual(before, snapshot(self.destination))
            self.assertFalse(terminal.screen.cursor.hidden)
            self.assertNotIn(terminal.screen.cursor.attrs.fg, {"brightred", "brightgreen", "brightblue"})

    def test_interactive_dry_runs_change_no_files(self):
        before = snapshot(self.destination)
        terminal = self.terminal("install", "--dry-run").ready()
        terminal.press("a")
        self.assertEqual(0, terminal.finish())
        self.assertEqual(before, snapshot(self.destination))
        self.cli("install")
        before = snapshot(self.destination)
        terminal = self.terminal("uninstall", "--dry-run").ready()
        terminal.press("a")
        self.assertEqual(0, terminal.finish())
        self.assertEqual(before, snapshot(self.destination))

    def test_corrupt_manifest_still_fails_before_prompting_and_preserves_bytes(self):
        self.cli("install")
        self.manifest.write_text("<<<<<<< HEAD\n{}\n=======\n{}\n>>>>>>> branch", encoding="utf-8")
        before = snapshot(self.destination)
        for verb in ("install", "uninstall"):
            for flags in ([], ["--json"], ["--dry-run"], ["-i"]):
                result = self.cli(verb, *flags, expected=1)
                self.assertEqual("", result.stdout)
                self.assertIn("Could not read the install manifest", result.stderr)
                self.assertEqual(before, snapshot(self.destination))
        self.cli("list", "--json")
        self.assertEqual(before, snapshot(self.destination))

    def test_redirected_interactive_and_json_combinations_remain_rejected(self):
        for verb in ("install", "uninstall"):
            if verb == "uninstall":
                self.cli("install")
            result = self.cli(verb, "-i", expected=1)
            self.assertIn("needs a terminal", result.stderr)
            result = self.cli(verb, "-i", "--json", expected=1)
            self.assertIn("cannot be combined", result.stderr)

    def test_empty_uninstall_does_not_prompt(self):
        before = snapshot(self.destination)
        result = self.cli("uninstall", "-i")
        self.assertIn("Nothing to remove", result.stdout)
        self.assertEqual(before, snapshot(self.destination))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tool", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    OPTIONS, unittest_args = parser.parse_known_args()
    OPTIONS.tool = OPTIONS.tool.resolve(strict=True)
    OPTIONS.artifacts = OPTIONS.artifacts.resolve()
    OPTIONS.artifacts.mkdir(parents=True, exist_ok=True)
    unittest.main(argv=[sys.argv[0], *unittest_args], verbosity=2)
