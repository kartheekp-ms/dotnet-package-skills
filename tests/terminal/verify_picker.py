"""Windows ConPTY regressions against a built or locally installed tool.

python verify_picker.py --tool <dotnet-package-skills.exe> --artifacts <directory>
"""

import argparse
import ctypes
from ctypes import wintypes
from contextlib import contextmanager
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


class ReflowEmulator:
    def __init__(self, rows, columns):
        self.process = subprocess.Popen(
            ["node", str(Path(__file__).with_name("emulator.cjs"))],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8",
        )
        try:
            self.state = self.send("open", rows=rows, columns=columns)
        except Exception:
            self.close()
            raise

    def send(self, action, **parameters):
        self.process.stdin.write(json.dumps({"action": action, **parameters}) + "\n")
        self.process.stdin.flush()
        line = self.process.stdout.readline()
        if not line:
            raise AssertionError("Terminal emulator failed: " + self.process.stderr.read())
        self.state = json.loads(line)
        return self.state

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)
        finally:
            self.process.stdout.close()
            self.process.stderr.close()


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
        self.emulator = None
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
            if self.emulator is not None:
                self.emulator.send("write", data=data)
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
            lambda: ASPIRE_HINT in self.compact and re.search(r"\[[ X]\]", self.text),
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
        if self.emulator is not None:
            self.emulator.send("resize", rows=rows, columns=columns)
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
        if self.emulator is not None:
            self.log.with_suffix(".buffers.json").write_text(
                json.dumps(self.emulator.state, indent=2), encoding="utf-8")
            self.emulator.close()


def snapshot(directory):
    return {
        str(path.relative_to(directory)): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in directory.rglob("*") if path.is_file()
    }

@contextmanager
def destination_mutex(destination):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
    kernel.CreateMutexW.restype = wintypes.HANDLE
    kernel.ReleaseMutex.argtypes = [wintypes.HANDLE]
    kernel.ReleaseMutex.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    canonical = os.path.realpath(destination).rstrip("\\/").upper()
    name = "Global\\dotnet-package-skills-" + hashlib.sha256(canonical.encode("utf-8")).hexdigest().upper()
    handle = kernel.CreateMutexW(None, True, name)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        yield
    finally:
        released = kernel.ReleaseMutex(handle)
        error = ctypes.get_last_error()
        kernel.CloseHandle(handle)
        if not released:
            raise ctypes.WinError(error)


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
        self.assertTrue(self.manifest.is_file())
        self.assertFalse(self.manifest.is_symlink())
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

    def prepare_project(self):
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
            "<TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>"
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
        return project_file, project_xml, beta_reference, restore

    def test_restored_solution_supports_descriptions_and_project_scoped_pruning(self):
        project_file, project_xml, beta_reference, restore = self.prepare_project()
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

    def test_target_picker_keeps_stale_skills_until_their_own_rows_are_unchecked(self):
        project_file, project_xml, beta_reference, restore = self.prepare_project()
        self.cli("install")
        before = snapshot(self.destination)
        project_file.write_text(project_xml.replace(beta_reference, ""), encoding="utf-8")
        restore()

        terminal = self.terminal().ready()
        self.assertIn("25 of 25 selected", terminal.compact)
        self.assertIn("0 to remove", terminal.compact)
        terminal.press(END, lambda: "beta-10" in terminal.focused, "retained stale skill")
        self.assertIn("[X]", terminal.focused)
        self.assertIn("Installed copy; kept unless you uncheck it.", terminal.compact)
        self.assertEqual(0, terminal.finish())
        self.assertEqual(before, snapshot(self.destination))

        terminal = self.terminal().ready()
        terminal.press(END, lambda: "beta-10" in terminal.focused, "explicit stale removal")
        terminal.press(SPACE, lambda: "1 to remove" in terminal.compact, "one visible removal")
        self.assertEqual(0, terminal.finish())
        self.assertEqual(set(self.names) - {"beta-10"}, self.installed())

    def test_empty_target_discovery_still_offers_existing_skills_and_never_implicitly_prunes(self):
        project_file, project_xml, beta_reference, restore = self.prepare_project()
        self.cli("install")
        before = snapshot(self.destination)
        project_file.write_text(
            project_xml.replace(beta_reference, "").replace(
                '<PackageReference Include="Demo.Alpha" Version="1.0.0" />', ""),
            encoding="utf-8",
        )
        restore()

        terminal = self.terminal().ready()
        self.assertIn("25 of 25 selected", terminal.compact)
        self.assertIn("0 to remove", terminal.compact)
        self.assertIn("Installed copy; kept unless you uncheck it.", terminal.compact)
        self.assertEqual(0, terminal.finish())
        self.assertEqual(before, snapshot(self.destination))

        terminal = self.terminal().ready()
        terminal.press(SPACE, lambda: "1 to remove" in terminal.compact, "explicit removal with no candidates")
        self.assertEqual(0, terminal.finish())
        self.assertEqual(set(self.names) - {"alpha-01"}, self.installed())

    def test_incomplete_target_discovery_fails_before_writes_in_all_install_modes(self):
        self.prepare_project()
        self.cli("install")
        before = snapshot(self.destination)
        (self.cache / "demo.beta").rename(self.cache / "demo.beta-moved-aside")
        available = self.cache / "demo.alpha" / "1.0.0" / "skills" / "alpha-01" / "SKILL.md"
        available.write_text("---\ndescription: Must not be copied during incomplete discovery.\n---\n", encoding="utf-8")

        for flags in ([], ["--dry-run"], ["--json"], ["-i"], ["-i", "--dry-run"]):
            with self.subTest(flags=flags):
                result = self.cli("install", *flags, expected=1)
                self.assertIn("resolved packages are missing", result.stderr)
                self.assertIn("Demo.Beta 2.0.0", result.stderr)
                self.assertEqual("", result.stdout)
                self.assertEqual(before, snapshot(self.destination))
        listed = json.loads(self.cli("list", "--json").stdout)
        self.assertEqual(["Demo.Beta 2.0.0"], listed["notOnDisk"])

        (self.cache / "demo.alpha").rename(self.cache / "demo.alpha-moved-aside")
        result = self.cli("install", "-i", expected=1)
        self.assertIn("resolved packages are missing", result.stderr)
        self.assertEqual(before, snapshot(self.destination))

    def add_shared_skills(self):
        for package, version in (("demo.alpha", "1.0.0"), ("demo.beta", "2.0.0")):
            directory = self.cache / package / version / "skills" / "shared-skill"
            directory.mkdir()
            (directory / "SKILL.md").write_text(
                f"---\ndescription: {package} owns this guidance.\n---\n", encoding="utf-8")

    def shared_owner(self):
        return next(
            item["package"] for item in json.loads(self.manifest.read_text(encoding="utf-8"))["installed"]
            if "shared-skill" in item["skills"]
        )

    def test_package_filtered_picker_cannot_offer_or_remove_another_packages_shared_name(self):
        self.add_shared_skills()
        self.cli("install", packages=["Demo.Alpha@1.0.0"])
        before = snapshot(self.destination)
        terminal = self.terminal(packages=["Demo.Beta@2.0.0"]).ready()
        self.assertIn("0 of 10 selected", terminal.compact)
        terminal.press(END, lambda: "beta-10" in terminal.focused, "last eligible Beta skill")
        self.assertNotIn("shared-skill", terminal.text)
        terminal.press("c")
        self.assertEqual(0, terminal.finish())
        self.assertEqual(before, snapshot(self.destination))
        self.assertEqual("Demo.Alpha", self.shared_owner())
        self.assertIn("Warning: skipped 1 colliding skill", terminal.text)

    def test_target_collision_preserves_ownership_until_explicit_uninstall(self):
        self.add_shared_skills()
        self.cli("install", packages=["Demo.Alpha@1.0.0"])
        shared = (self.destination / "shared-skill" / "SKILL.md").read_bytes()
        project_file, project_xml, _, restore = self.prepare_project()
        project_file.write_text(
            project_xml.replace('<PackageReference Include="Demo.Alpha" Version="1.0.0" />', ""),
            encoding="utf-8",
        )
        restore()

        result = json.loads(self.cli("install", "--json").stdout)
        self.assertEqual(1, len(result["skipped"]))
        self.assertEqual("shared-skill", result["skipped"][0]["skillName"])
        self.assertNotIn("shared-skill", {entry["skillName"] for entry in result["removed"]})
        self.assertEqual("Demo.Alpha", self.shared_owner())
        self.assertEqual(shared, (self.destination / "shared-skill" / "SKILL.md").read_bytes())

        self.cli("uninstall", "--package", "Demo.Alpha")
        self.cli("install")
        self.assertEqual("Demo.Beta", self.shared_owner())

    def test_blank_and_missing_uninstall_filters_cannot_broaden_removal(self):
        self.cli("install")
        before = snapshot(self.destination)
        for value in ("", " ", "\t"):
            for flags in ([], ["--json"], ["--dry-run"], ["-i"]):
                with self.subTest(value=value, flags=flags):
                    result = self.cli("uninstall", "--package", value, *flags, expected=1)
                    self.assertIn("non-empty package ID", result.stderr)
                    self.assertEqual(before, snapshot(self.destination))
        for flags in ([], ["--dry-run"], ["--json"], ["-i"]):
            result = self.cli("uninstall", "--package", *flags, expected=1)
            self.assertTrue(result.stderr.strip())
            self.assertEqual(before, snapshot(self.destination))
        for repeated in ("--package", "-p", "--package=", "--package=Demo.Alpha"):
            result = self.cli(
                "uninstall", "--json", "--package", "Demo.Alpha", repeated, expected=1)
            self.assertTrue(result.stderr.strip())
            self.assertEqual(before, snapshot(self.destination))

    def test_existing_ownership_data_cannot_be_missing_duplicated_or_ambiguous(self):
        self.cli("install")
        original = self.manifest.read_text(encoding="utf-8")
        duplicate = json.loads(original)
        duplicate["installed"].append(
            {"package": "Other.Owner", "version": "1.0.0", "skills": ["ALPHA-01"]})
        for damaged in ("{}", '{"installed":[],"Installed":[]}', json.dumps(duplicate)):
            self.manifest.write_text(damaged, encoding="utf-8")
            before = snapshot(self.destination)
            for verb, flags in (
                ("install", []), ("install", ["-i"]), ("install", ["--dry-run"]),
                ("uninstall", ["--package", "Demo.Alpha"]), ("uninstall", ["-i"]),
            ):
                with self.subTest(damaged=damaged, verb=verb, flags=flags):
                    result = self.cli(verb, *flags, expected=1)
                    self.assertIn("Could not read the install manifest", result.stderr)
                    self.assertEqual(before, snapshot(self.destination))
            self.cli("list", "--json")

    def test_unsafe_manifest_names_fail_before_prompting_or_writing(self):
        self.prepare_project()
        self.cli("install")
        original = self.manifest.read_text(encoding="utf-8")
        for unsafe_name in ("...", ".. ", "team-owned.", "team-owned "):
            damaged = json.loads(original)
            damaged["installed"].append(
                {"package": "Unsafe.Owner", "version": "1.0.0", "skills": [unsafe_name]})
            self.manifest.write_text(json.dumps(damaged), encoding="utf-8")
            before = snapshot(self.destination)
            for verb in ("install", "uninstall"):
                for flags in ([], ["--json"], ["--dry-run"], ["--dry-run", "--json"],
                              ["-i"], ["-i", "--dry-run"]):
                    with self.subTest(unsafe_name=unsafe_name, verb=verb, flags=flags):
                        result = self.cli(verb, *flags, expected=1)
                        self.assertEqual("", result.stdout)
                        self.assertIn("not a safe skill folder name", result.stderr)
                        self.assertIn("No skills were changed", result.stderr)
                        self.assertIn("preserved", result.stderr)
                        self.assertEqual(before, snapshot(self.destination))
            self.cli("list", "--json")
            self.assertEqual(before, snapshot(self.destination))

    def test_current_owner_refresh_is_not_blocked_by_an_earlier_named_collision(self):
        self.add_shared_skills()
        self.cli("install", packages=["Demo.Beta@2.0.0"])
        owned_source = self.cache / "demo.beta" / "2.0.0" / "skills" / "shared-skill" / "SKILL.md"
        owned_source.write_text("---\ndescription: Updated owner guidance.\n---\n", encoding="utf-8")
        self.prepare_project()

        preview = json.loads(self.cli("install", "--dry-run", "--json").stdout)
        shared = next(skill for skill in preview["skills"] if skill["skillName"] == "shared-skill")
        self.assertEqual("Demo.Beta", shared["packageId"])
        terminal = self.terminal().ready()
        terminal.press(END, lambda: "shared-skill" in terminal.focused, "existing owner candidate")
        self.assertIn("Updated owner guidance.", terminal.compact)
        self.assertNotIn("Installed copy;", terminal.focused)
        self.assertEqual(0, terminal.finish())
        self.assertEqual("Demo.Beta", self.shared_owner())
        self.assertIn("Updated owner guidance.", (self.destination / "shared-skill" / "SKILL.md").read_text())

    def test_repeated_equivalent_coordinates_do_not_self_collide(self):
        result = json.loads(self.cli(
            "install", "--json",
            packages=["Demo.Alpha@1.0", "demo.alpha@1.0.0", "Demo.Alpha@1.0.0.0"]).stdout)
        self.assertEqual(1, result["packagesScanned"])
        self.assertEqual(15, len(result["skills"]))
        self.assertEqual([], result["skipped"])

    def test_install_reports_removed_tracking_even_when_the_folder_is_already_gone(self):
        self.prepare_project()
        self.cli("install")
        target = self.destination / "beta-10"
        for file in target.iterdir():
            file.unlink()
        target.rmdir()
        terminal = self.terminal().ready()
        terminal.focus("beta-10", 24)
        terminal.press(SPACE, lambda: "1 to remove" in terminal.compact, "remove missing tracked folder")
        self.assertEqual(0, terminal.finish())
        self.assertIn("Removed 1 skill", terminal.text)
        self.assertEqual(set(self.names) - {"beta-10"}, self.installed())

    def test_valid_underscore_ids_work_for_coordinate_and_uninstall_filters(self):
        for package in ("_Acme", "Acme_"):
            source = self.cache / package.lower() / "1.0.0" / "skills" / "underscore-example"
            source.mkdir(parents=True)
            (source / "SKILL.md").write_text("---\ndescription: Underscore package.\n---\n", encoding="utf-8")
            self.cli("install", packages=[f"{package}@1.0.0"])
            terminal = self.terminal("uninstall", "--package", package).ready()
            terminal.press(SPACE)
            self.assertEqual(0, terminal.finish())
            self.assertEqual(set(), self.installed())

    def test_interactive_uninstall_matches_the_same_normalized_version_as_noninteractive(self):
        self.cli("install", packages=["Demo.Alpha@1.0.0"])
        before = snapshot(self.destination)
        preview = json.loads(self.cli(
            "uninstall", "--package", "demo.alpha@1.0", "--dry-run", "--json").stdout)
        self.assertEqual(15, len(preview["removed"]))

        terminal = self.terminal("uninstall", "--package", "demo.alpha@1.0", "--dry-run").ready()
        self.assertIn("0 of 15 selected", terminal.compact)
        terminal.press("a", lambda: "15 to remove" in terminal.compact, "matching normalized version")
        self.assertEqual(0, terminal.finish())
        self.assertEqual(before, snapshot(self.destination))

        terminal = self.terminal("uninstall", "--package", "Demo.Alpha@1.0.0.0").ready()
        terminal.press("a")
        self.assertEqual(0, terminal.finish())
        self.assertEqual(set(), self.installed())

    def test_ownership_changes_while_a_picker_is_open_are_rejected(self):
        self.add_shared_skills()
        for verb in ("install", "uninstall"):
            with self.subTest(verb=verb):
                self.cli("uninstall")
                self.cli("install", packages=["Demo.Alpha@1.0.0"])
                terminal = self.terminal(verb, packages=["Demo.Alpha@1.0.0"]).ready()
                terminal.press(END, lambda: "shared-skill" in terminal.focused, "reviewed owner")
                terminal.press(SPACE)
                self.cli("uninstall", "--package", "Demo.Alpha")
                self.cli("install", packages=["Demo.Beta@2.0.0"])
                before = snapshot(self.destination)
                self.assertEqual(1, terminal.finish())
                self.assertIn("ownership changed", terminal.compact)
                self.assertEqual(before, snapshot(self.destination))

    def test_destination_writes_are_serialized_across_processes(self):
        self.cli("install")
        for verb in ("install", "uninstall"):
            with self.subTest(verb=verb):
                before = snapshot(self.destination)
                process = None
                try:
                    command = self.command(verb, "--json")
                    if verb == "uninstall":
                        command[command.index("--destination") + 1] = "\\\\?\\" + str(self.destination)
                    with destination_mutex(self.destination):
                        process = subprocess.Popen(
                            command, cwd=self.root, env=self.environment,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding="utf-8",
                        )
                        with self.assertRaises(subprocess.TimeoutExpired):
                            process.wait(timeout=1)
                        self.assertEqual(before, snapshot(self.destination))
                    stdout, stderr = process.communicate(timeout=30)
                    self.assertEqual(0, process.returncode, stdout + stderr)
                    self.assertIsInstance(json.loads(stdout), dict)
                finally:
                    if process is not None and process.poll() is None:
                        process.kill()
                        process.communicate(timeout=10)
        self.assertEqual(set(), self.installed())

    def test_a_destination_alias_removed_while_waiting_invalidates_the_operation(self):
        self.cli("install")
        before = snapshot(self.destination)
        alias = self.root / "destination junction"
        result = subprocess.run(
            ["powershell.exe", "-NoLogo", "-NoProfile", "-Command",
             f"New-Item -ItemType Junction -Path '{alias}' -Target '{self.destination}' | Out-Null"],
            capture_output=True, encoding="utf-8", timeout=15,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        process = None
        try:
            command = self.command("install", "--json")
            command[command.index("--destination") + 1] = str(alias)
            with destination_mutex(self.destination):
                process = subprocess.Popen(
                    command, cwd=self.root, env=self.environment,
                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding="utf-8",
                )
                with self.assertRaises(subprocess.TimeoutExpired):
                    process.wait(timeout=1)
                alias.rmdir()
            stdout, stderr = process.communicate(timeout=30)
            self.assertEqual(1, process.returncode, stdout + stderr)
            self.assertIn("changed while waiting", stderr)
            self.assertFalse(alias.exists())
            self.assertEqual(before, snapshot(self.destination))
        finally:
            if process is not None and process.poll() is None:
                process.kill()
                process.communicate(timeout=10)
            if alias.exists():
                alias.rmdir()

    def test_long_destination_paths_remain_manageable_after_the_first_install(self):
        destination = self.root
        for _ in range(5):
            destination /= "a" * 60
        self.destination = destination / "skills"
        self.manifest = self.destination / ".dotnet-package-skills.json"
        self.assertGreater(len(str(self.destination)), 260)

        self.cli("install")
        self.assertEqual(set(self.names), self.installed())
        self.cli("install")
        before = snapshot(self.destination)
        self.cli("uninstall", "--dry-run")
        self.assertEqual(before, snapshot(self.destination))
        self.cli("uninstall")
        self.assertFalse(self.manifest.exists())

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
        terminal.press(SPACE, lambda: "[X]" in terminal.focused, "pending install")
        self.assertEqual({"brightblue"}, set(terminal.colors("alpha-01")))
        self.assertEqual({"brightblue"}, set(terminal.colors("[X]")))
        self.assertEqual({"brightblue"}, set(terminal.colors(">")))
        self.assertEqual({"brightblue"}, set(terminal.colors("ALPHA-FIRST")))
        terminal.press(DOWN, lambda: "alpha-02" in terminal.focused, "move focus off the checked skill")
        self.assertNotIn("brightblue", terminal.colors("alpha-01"))
        self.assertEqual("brightblue", terminal.colors("[X]")[1])
        self.assertNotIn("brightblue", terminal.colors("ALPHA-FIRST"))
        terminal.press(UP, lambda: "alpha-01" in terminal.focused, "return to checked skill")
        terminal.press(SPACE, lambda: "[ ]" in terminal.focused, "undo install")
        self.assertNotIn("brightgreen", terminal.colors("alpha-01"))
        self.assertEqual(0, terminal.finish(ESC))
        self.assertFalse(self.manifest.exists())

    def test_focus_colors_all_wrapped_lines_and_checked_mark_stays_blue_off_focus(self):
        terminal = self.terminal().ready()
        terminal.press(DOWN, lambda: "alpha-02" in terminal.focused, "focus multiline skill")
        first = next(i for i, line in enumerate(terminal.screen.display) if "alpha-02 - " in line)
        self.assertTrue(terminal.screen.display[first + 1].strip())
        for row in (first, first + 1):
            self.assertEqual(
                {"brightblue"},
                {cell.fg for cell in terminal.screen.buffer[row].values() if cell.data.strip()},
            )
        terminal.press(SPACE, lambda: "[X]" in terminal.focused, "check multiline skill")
        terminal.press(DOWN, lambda: "alpha-03" in terminal.focused, "move off multiline skill")
        self.assertNotIn("brightblue", terminal.colors("alpha-02"))
        self.assertNotIn("brightblue", terminal.colors("Guidance for alpha-02"))
        self.assertEqual("brightblue", terminal.colors("[X]")[1])
        self.assertEqual(0, terminal.finish(ESC))

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
        self.assertIn("[X]", terminal.focused)
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

    def test_reopening_preselects_installed_items_and_marks_removal_with_red_brackets(self):
        self.cli("install", packages=["Demo.Alpha@1.0.0"])
        before = snapshot(self.destination)
        terminal = self.terminal().ready()
        self.assertIn("[X]", terminal.focused)
        self.assertNotIn("brightgreen", terminal.colors("alpha-01"))
        terminal.press(SPACE, lambda: "[ ]" in terminal.focused, "pending removal")
        brackets = terminal.colors("[ ]")
        self.assertEqual("brightred", brackets[0])
        self.assertEqual("brightred", brackets[2])
        self.assertEqual({"brightblue"}, set(terminal.colors("alpha-01")))
        terminal.press(SPACE, lambda: "[X]" in terminal.focused, "keep installed")
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
        terminal.press(SPACE, lambda: "[X]" in terminal.focused, "select removal")
        self.assertEqual(["brightred", "brightblue", "brightred"], terminal.colors("[X]"))
        self.assertEqual({"brightblue"}, set(terminal.colors("beta-01")))
        terminal.press(DOWN, lambda: "beta-02" in terminal.focused, "leave the removal checked")
        self.assertNotIn("brightred", terminal.colors("beta-01"))
        self.assertNotIn("brightblue", terminal.colors("beta-01"))
        self.assertEqual(["brightred", "brightblue", "brightred"], terminal.colors("[X]"))
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
        self.assertIn("[X]", terminal.focused)
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

    def test_first_picker_frame_starts_at_the_top_after_prior_shell_output(self):
        coordinate = "Demo.Compact@1.0.0"
        for name in ("compact-01", "compact-02", "compact-03"):
            directory = self.cache / "demo.compact" / "1.0.0" / "skills" / name
            directory.mkdir(parents=True)
            (directory / "SKILL.md").write_text(
                "---\ndescription: Short guidance for a compact checklist.\n---\n",
                encoding="utf-8",
            )
        for verb in ("install", "uninstall"):
            if verb == "uninstall":
                self.cli("install", packages=[coordinate])
            for no_color in (False, True):
                with self.subTest(verb=verb, no_color=no_color):
                    before = snapshot(self.destination)
                    terminal = self.terminal(
                        verb, "--dry-run", packages=[coordinate], rows=50, columns=140,
                        no_color=no_color, powershell=True,
                    )
                    terminal.emulator = ReflowEmulator(50, 140)
                    terminal.ready()
                    state = terminal.emulator.state
                    self.assertTrue(state["alternate"])
                    self.assertTrue(state["active"].startswith("Which skills should"), state["active"])
                    self.assertNotIn("Which skills should", state["normal"])
                    self.assertIn("earlier console output", state["normal"])
                    self.assertEqual(0, terminal.finish(ESC))
                    self.assertFalse(terminal.emulator.state["alternate"])
                    self.assertIn("earlier console output", terminal.emulator.state["normal"])
                    self.assertIn("Cancelled.", terminal.emulator.state["normal"])
                    self.assertEqual(before, snapshot(self.destination))

    def test_resize_reflow_does_not_leave_duplicate_picker_frames_in_normal_scrollback(self):
        before = snapshot(self.destination)
        terminal = self.terminal(rows=70, columns=210, powershell=True)
        terminal.emulator = ReflowEmulator(70, 210)
        terminal.ready()
        terminal.press(SPACE, lambda: "1 of 25" in terminal.compact, "selected before host reflow")
        terminal.resize(24, 80)
        terminal.wait_for(
            lambda: ASPIRE_HINT in terminal.compact and "1 of 25" in terminal.compact
            and terminal.page_count > 1,
            "host-reflowed narrow picker",
        )

        state = terminal.emulator.state
        self.assertNotIn("Which skills should be installed?", state["normal"])
        self.assertTrue(state["alternate"])
        self.assertEqual(1, state["active"].count("Which skills should be installed?"))
        terminal.resize(50, 160)
        terminal.wait_for(lambda: ASPIRE_HINT in terminal.compact and "[X]" in terminal.focused, "grown picker")
        self.assertNotIn("Which skills should be installed?", terminal.emulator.state["normal"])
        self.assertEqual(0, terminal.finish(ESC))
        state = terminal.emulator.state
        self.assertFalse(state["alternate"])
        self.assertNotIn("Which skills should be installed?", state["normal"])
        self.assertIn("earlier console output", state["normal"])
        self.assertIn("Cancelled.", state["normal"])
        self.assertEqual(before, snapshot(self.destination))

    def test_interactive_screen_restores_shell_output_on_accept_cancel_and_interrupt(self):
        for verb in ("install", "uninstall"):
            if verb == "uninstall":
                self.cli("install")
            for key in (ENTER, ESC, CTRL_C):
                with self.subTest(verb=verb, key=repr(key)):
                    before = snapshot(self.destination)
                    terminal = self.terminal(verb, "--dry-run", rows=32, columns=100, powershell=True)
                    terminal.emulator = ReflowEmulator(32, 100)
                    terminal.ready()
                    self.assertTrue(terminal.emulator.state["alternate"])
                    terminal.press("a")
                    self.assertEqual(0, terminal.finish(key))
                    state = terminal.emulator.state
                    self.assertFalse(state["alternate"])
                    self.assertIn("earlier console output", state["normal"])
                    self.assertNotIn("Which skills should", state["normal"])
                    self.assertIn("Cancelled." if key != ENTER else "Would", state["normal"])
                    self.assertEqual(before, snapshot(self.destination))

    def test_no_color_mode_exposes_action_markers_without_color(self):
        terminal = self.terminal(no_color=True).ready()
        terminal.press(SPACE, lambda: "[X]" in terminal.focused, "no-color installation")
        self.assertIn("+", terminal.focused)
        self.assertNotIn("brightgreen", terminal.colors("alpha-01"))
        self.assertNotIn("brightblue", terminal.colors(">"))
        self.assertEqual(0, terminal.finish())
        terminal = self.terminal("uninstall", no_color=True).ready()
        terminal.press(SPACE, lambda: "[X]" in terminal.focused, "no-color removal")
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

    def test_manifest_metadata_cannot_inject_terminal_controls_into_human_reports(self):
        self.cli("install")
        original = self.manifest.read_text(encoding="utf-8")
        controls = (
            "\x1b]52;c;ZWNobyBleGFtcGxl\x07",
            "\x1b]52;c;ZWNobyBleGFtcGxl\x1b\\",
            "\x9d52;c;ZWNobyBleGFtcGxl\x9c",
        )
        for control in controls:
            damaged = json.loads(original)
            owner = damaged["installed"][0]
            owner["package"] += control
            owner["version"] += control
            self.manifest.write_text(json.dumps(damaged), encoding="utf-8")
            before = snapshot(self.destination)
            for verb in ("uninstall", "install"):
                with self.subTest(verb=verb, control=ascii(control)):
                    result = self.cli(verb, "--dry-run")
                    self.assert_plain_output(result.stdout)
                    self.assertIn("Demo.Alpha", result.stdout)
                    self.assertEqual(before, snapshot(self.destination))
                    report = json.loads(self.cli(verb, "--dry-run", "--json").stdout)
                    if verb == "uninstall":
                        identity = next(skill for skill in report["removed"] if skill["skillName"] == "alpha-01")
                        self.assertEqual(owner["package"], identity["packageId"])
                        self.assertEqual(owner["version"], identity["packageVersion"])
                    else:
                        self.assertTrue(any(owner["package"] in skill["reason"] for skill in report["skipped"]))
                    self.assertEqual(before, snapshot(self.destination))
        result = self.cli("uninstall")
        self.assert_plain_output(result.stdout)
        self.assertIn("Removed 25 skills", result.stdout)
        self.assertTrue((self.destination / "team-owned" / "SKILL.md").is_file())

    def test_operational_errors_cannot_emit_terminal_controls(self):
        before = snapshot(self.destination)
        result = self.cli(
            "install", packages=["Example\x1b]52;c;ZWNobyBleGFtcGxl\x07"], expected=1)

        self.assertEqual("", result.stdout)
        self.assert_plain_output(result.stderr)
        self.assertIn("error:", result.stderr)
        self.assertIn("Id@Version", result.stderr)
        self.assertEqual(before, snapshot(self.destination))

    def test_parser_diagnostics_cannot_emit_controls_on_either_output_stream(self):
        before = snapshot(self.destination)
        controls = (
            "\x1b]52;c;ZWNobyBleGFtcGxl\x07",
            "\x1b]52;c;ZWNobyBleGFtcGxl\x1b\\",
            "\x9d52;c;ZWNobyBleGFtcGxl\x9c",
        )
        for control in controls:
            for arguments in (
                ["--package", "Ex" + control + "ample"],
                ["--package", "Demo.Alpha@1.*" + control],
                ["--unknown" + control],
                ["--dry-run=false" + control],
            ):
                with self.subTest(arguments=ascii(arguments)):
                    result = self.cli("uninstall", *arguments, expected=1)
                    self.assert_plain_output(result.stdout)
                    self.assert_plain_output(result.stderr)
                    self.assertNotIn("ZWNobyBleGFtcGxl", result.stdout + result.stderr)
                    self.assertEqual(before, snapshot(self.destination))
        typo = subprocess.run(
            [str(OPTIONS.tool), "uninstal\x1b"], cwd=self.root, env=self.environment,
            capture_output=True, encoding="utf-8", timeout=30,
        )
        self.assertEqual(1, typo.returncode)
        self.assertIn("Did you mean", typo.stdout)
        self.assert_plain_output(typo.stdout)
        self.assert_plain_output(typo.stderr)
        self.assertEqual(before, snapshot(self.destination))

    def assert_plain_output(self, text):
        self.assertFalse(
            any(unicodedata.category(character) == "Cc" and character not in "\r\n"
                or character in "\u202e\u2066" for character in text),
            "Captured output contains unsafe terminal controls.",
        )

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
