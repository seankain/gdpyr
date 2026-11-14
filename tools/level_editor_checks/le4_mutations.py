#!/usr/bin/env python3
"""LE4's engine-free rules, each held by a test that fails when the rule is broken: the editor's
commands -- a stroke's dabs, its end, its undo, a ramp, the bands and the text -- what a save, an
autosave, the list and a fork leave on the disk, which key does what and what a brush shows, the
status line and the readout, the autosave's clock, the test process's arguments, and --edit
(docs/LEVEL_EDITOR.md §8, §9.2).

    python3 tools/level_editor_checks/le4_mutations.py              # every rule, three at a time
    python3 tools/level_editor_checks/le4_mutations.py stroke key   # the ones whose name holds a word
    python3 tools/level_editor_checks/le4_mutations.py --jobs 1

le3_mutations.py's way: it copies what the tests compile and read to a scratch directory, and there,
for each rule below, breaks the rule -- one edit to one file -- and runs the test classes LE4 added or
changed. One line a rule: PASS with the tests that failed, or FAIL when every test still passed, which
is a claim nothing holds. The tree it was started from is not written to.

A rule's edit is a piece of the source as it stands and what replaces it. A piece that is no longer
in the file once, exactly, stops the run: the source has moved and the list wants bringing along.

The standard library and `dotnet`; nothing else.
"""
import concurrent.futures
import os
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CLASSES = ("MapEditSessionTests", "MapStoreTests", "MapEditorControlsTests", "EditLaunchTests", "EditHistoryTests",
           "LaunchOptionsTests", "MapLaunchTests")
FILTER = "|".join(f"FullyQualifiedName~Gdpyr.Tests.{name}" for name in CLASSES)

SESSION = "Scripts/Sim/Level/MapEditSession.cs"
STORE = "Scripts/Sim/Level/MapStore.cs"
CONTROLS = "Scripts/MapEditor/MapEditorControls.cs"
LAUNCH = "Scripts/Core/LaunchOptions.cs"
HISTORY = "Scripts/Sim/Level/EditHistory.cs"

# (name, file, the rule as broken, the source as it stands, what it is replaced with)
RULES = [
    # ---- a stroke (§8.3, §8.6) ---------------------------------------------------------------------
    ("stroke-rate", SESSION, "a held brush dabbing 20 times a second, not 30",
     "public const int DabsPerSecond = 30;", "public const int DabsPerSecond = 20;"),
    ("stroke-cap", SESSION, "a stalled frame's backlog laid down whole",
     "_owed = Math.Min(_owed + (seconds * DabsPerSecond), MaxDabsAtOnce);", "_owed += seconds * DabsPerSecond;"),
    ("stroke-click", SESSION, "a click that does not dab",
     "\t\tStroking = true;\n\t\tDabAt(x, z);\n", "\t\tStroking = true;\n"),
    ("stroke-dt", SESSION, "a dab given a sixtieth of a second, not its thirtieth",
     "TerrainBrush.Dab(Document.Terrain, _stroke, x, z, DabSeconds)", "TerrainBrush.Dab(Document.Terrain, _stroke, x, z, 1f / 60f)"),
    ("stroke-round", SESSION, "a stroke's end not rounded to stored steps",
     "\t\tStroking = false;\n\t\tDocument.Terrain.Round(_touched);\n", "\t\tStroking = false;\n"),
    ("stroke-pads", SESSION, "the pads not kept at a stroke's end",
     "SampleRect kept = PrefabPlacement.KeepPads(Document, area);", "SampleRect kept = SampleRect.Empty;"),
    ("stroke-step", SESSION, "a stroke whose dabs rounded back kept as a step",
     "bool step = !changed.IsEmpty;", "bool step = !area.IsEmpty;"),
    ("stroke-edit-rect", SESSION, "the step to undo holding one column less than what changed",
     "History.Push(MapEdit.Terrain(_before, Document.Terrain, changed));",
     "History.Push(MapEdit.Terrain(_before, Document.Terrain, new SampleRect(changed.MinX + 1, changed.MinZ, changed.MaxX, changed.MaxZ)));"),
    ("stroke-cancel", SESSION, "a stroke dropped and its ground left",
     "field[x, z] = _before[x, z];", "field[x, z] = field[x, z];"),
    ("stroke-twice", SESSION, "a second stroke begun while one is under way",
     "if (Stroking || _brush.Kind == BrushKind.Ramp ||", "if (_brush.Kind == BrushKind.Ramp ||"),
    ("stroke-ramp", SESSION, "a ramp brush dabbed as a stroke",
     "if (Stroking || _brush.Kind == BrushKind.Ramp ||", "if (Stroking ||"),
    ("stroke-undo-during", SESSION, "an undo in the middle of a stroke",
     "if (Stroking || edit == null || !History.Undo(Document))", "if (edit == null || !History.Undo(Document))"),
    ("stroke-dirty", SESSION, "a save forgotten by the next change",
     "public void MarkSaved() => Dirty = false;", "public void MarkSaved() => Dirty = Dirty;"),
    ("stroke-report", SESSION, "the validator's report kept after a change",
     "if (_report == null || _reportAt != Changes)", "if (_report == null)"),

    # ---- a ramp, the bands, the text (§6.2, §8.3, §9.2) ---------------------------------------------
    ("ramp-width", SESSION, "a ramp as wide as the ring's radius, not its diameter",
     "2f * _brush.RadiusMeters,", "_brush.RadiusMeters,"),
    ("ramp-round", SESSION, "a ramp not rounded to stored steps",
     "\t\t\tDocument.Terrain.Round(touched);\n\t\t\toutcome = Keep(touched);", "\t\t\toutcome = Keep(touched);"),
    ("bands-meaning", SESSION, "band numbers out of their order taken",
     "if (bands.Equals(Document.Bands) || !bands.Settings.IsValid)", "if (bands.Equals(Document.Bands))"),
    ("bands-unsaved", SESSION, "a slider moved and the map still saved",
     "\t\tDocument.Bands = bands;\n\t\tTouch();", "\t\tDocument.Bands = bands;"),
    ("text-control", SESSION, "a control character taken into a map's text",
     "if (char.IsControl(c) && !(lineBreaks && c == '\\n'))", "if (false && char.IsControl(c))"),
    ("text-bytes", SESSION, "a text's length counted in characters, not the bytes a file holds",
     "if (MapDocument.Utf8Bytes(text) > maxBytes)", "if (text.Length > maxBytes)"),

    # ---- undo's next step (§8.6) ------------------------------------------------------------------
    ("history-next-undo", HISTORY, "the oldest step said to be the one an undo reverts",
     "public MapEdit NextUndo => _done.Last?.Value;", "public MapEdit NextUndo => _done.First?.Value;"),
    ("history-next-redo", HISTORY, "no step to redo, ever",
     "public MapEdit NextRedo => _undone.Count > 0 ? _undone.Peek() : null;", "public MapEdit NextRedo => null;"),

    # ---- saving (§8.6, §9.2) ------------------------------------------------------------------------
    ("save-revision", STORE, "a save that is not the next revision",
     "document.Revision = revision == uint.MaxValue ? revision : revision + 1;", "document.Revision = revision;"),
    ("save-modified", STORE, "a save that does not say when",
     "\t\tdocument.ModifiedUnixSeconds = unixSeconds;\n\t\tif (!MapFile.TryWrite", "\t\tif (!MapFile.TryWrite"),
    ("save-failed", STORE, "a save that failed leaving the next revision in the document",
     "\t\t\tdocument.Revision = revision;\n\t\t\tdocument.ModifiedUnixSeconds = modified;\n", ""),
    ("save-backup", STORE, "the last file not kept as .bak",
     "\t\t\t\tFile.Copy(path, backup, true);\n", ""),
    ("save-rename", STORE, "the new file copied over the map and left beside it as .tmp",
     "File.Move(temp, path, true);", "File.Copy(temp, path, true);"),
    ("save-autosave", STORE, "a save that leaves the autosave to be offered",
     "\t\tDiscardAutosave(directory, name);\n\t\treturn true;\n\t}\n\n\t/// <summary>\n\t/// Writes a document as the autosave",
     "\t\treturn true;\n\t}\n\n\t/// <summary>\n\t/// Writes a document as the autosave"),
    ("autosave-same", STORE, "an autosave that is the map as saved offered all the same",
     "if (TryReadFile(PathOf(directory, name), out MapDocument saved, out _, out _) && saved.SameAs(document, out _))",
     "if (false)"),
    ("list-names", STORE, "a file that is not a library name listed",
     "if (!MapNames.TryParseName(stem, out string name, out _) || name != stem)",
     "if (!MapNames.TryParseName(stem.ToLowerInvariant(), out string name, out _))"),
    ("list-unread", STORE, "a file that does not read left out of the list",
     "\t\t\t\tmaps.Add(new StoredMap(name, path, null, error, null, null, autosave));\n", ""),
    ("exists-name", STORE, "a name taken as a path",
     "MapNames.TryParseName(name, out string parsed, out _) && File.Exists(PathOf(directory, parsed))",
     "name != null && File.Exists(PathOf(directory, name))"),
    ("fork-parent", STORE, "a fork that is its own parent",
     "document.ParentId = document.Id;", "document.ParentId = id;"),
    ("fork-revision", STORE, "a fork that keeps its source's revision",
     "\t\tdocument.Id = id;\n\t\tdocument.Revision = 0;\n", "\t\tdocument.Id = id;\n"),
    ("fork-hash", STORE, "a fork that forgets its parent's hash",
     "document.ParentHash = hash.Length == MapDocument.HashBytes ? hash.ToArray() : null;", "document.ParentHash = null;"),

    # ---- the keys and the panel (§8.2, §8.3) -------------------------------------------------------
    ("key-undo", CONTROLS, "undo on Z, without Ctrl",
     "new(Key.Z, true, false, EditorAction.Undo),", "new(Key.Z, false, false, EditorAction.Undo),"),
    ("key-shift", CONTROLS, "a key that does nothing with Shift held",
     "return shift ? unshifted : EditorAction.None;", "return EditorAction.None;"),
    ("fields-locked", CONTROLS, "a grade-locked brush showing a core it does not read, and not its grade",
     "LockedFields = { BrushField.Radius, BrushField.Grade, BrushField.Rate };",
     "LockedFields = { BrushField.Radius, BrushField.Core, BrushField.Rate };"),
    ("amount-soft", CONTROLS, "- and = stepping the rate of a brush that reads its strength",
     "BrushKind.Smooth or BrushKind.Level => BrushField.Strength,", "BrushKind.Smooth or BrushKind.Level => BrushField.Rate,"),
    ("shape-locked", CONTROLS, ", and . stepping the core of a brush that reads its grade",
     "BrushKind.GradeLocked => BrushField.Grade,\n\t\tBrushKind.Sculpt or", "BrushKind.GradeLocked => BrushField.Core,\n\t\tBrushKind.Sculpt or"),
    ("step-down", CONTROLS, "[ widening the brush as ] does",
     "direction > 0 ? value * ScaleStep : value / ScaleStep", "value * ScaleStep"),
    ("step-share", CONTROLS, "a share stepped off its twentieths",
     "\t\t\tvalue = MathF.Round(value / ShareStep) * ShareStep;\n", ""),
    ("band-sand", CONTROLS, "sand's top slid past mud's",
     "s.SandTopMeters = MathF.Min(n, s.MudTopMeters);", "s.SandTopMeters = n;"),
    ("band-mud", CONTROLS, "mud's top slid out from between its neighbours",
     "s.MudTopMeters = Math.Clamp(n, s.SandTopMeters, s.GrassTopMeters);", "s.MudTopMeters = n;"),
    ("slope-35", CONTROLS, "a face of 35° coloured as walkable ground under it",
     "degrees < SteepDegrees ? SlopeClass.Walkable", "degrees <= SteepDegrees ? SlopeClass.Walkable"),
    ("slope-40", CONTROLS, "a face of 40° coloured as past what is walkable",
     "degrees <= MapLimits.WalkableSlopeDegrees ? SlopeClass.Steep", "degrees < MapLimits.WalkableSlopeDegrees ? SlopeClass.Steep"),
    ("overlay-cycle", CONTROLS, "O never reaching the contours",
     "TerrainOverlay.Buildable => TerrainOverlay.Contours,", "TerrainOverlay.Buildable => TerrainOverlay.None,"),
    ("readout-disc", CONTROLS, "the readout reading a square, not the brush's disc",
     "if ((dx * dx) + (dz * dz) <= radius * radius)", "if (true)"),
    ("readout-crease", CONTROLS, "the readout reading a cell's first triangle only",
     "steepest = MathF.Max(steepest, MathF.Max(field.FaceGrade(cx, cz, false), field.FaceGrade(cx, cz, true)));",
     "steepest = MathF.Max(steepest, field.FaceGrade(cx, cz, false));"),
    ("test-path", CONTROLS, "a test process that is not told the project's path",
     "\t\t\targuments.Add(\"--path\");\n\t\t\targuments.Add(projectPath);\n", ""),
    ("test-headless", CONTROLS, "a headless editor's test process opening a window",
     "\t\t\targuments.Add(\"--headless\");\n", ""),
    ("test-order", CONTROLS, "the game's flags put before the bare --",
     "\t\targuments.Add(\"--\");\n\t\targuments.Add(\"--practice\");", "\t\targuments.Add(\"--practice\");\n\t\targuments.Add(\"--\");"),
    ("autosave-minute", CONTROLS, "an autosave due only after a minute has gone by, not at it",
     "return _waiting >= MapEditorControls.AutosaveSeconds;", "return _waiting > MapEditorControls.AutosaveSeconds;"),
    ("autosave-negative", CONTROLS, "a step back in time counted",
     "_waiting += seconds > 0f ? seconds : 0f;", "_waiting += seconds;"),

    # ---- --edit (§8.1) -----------------------------------------------------------------------------
    ("edit-flag", LAUNCH, "--edit parsed and forgotten", "\t\t\tEdit = edit,\n", "\t\t\tEdit = false,\n"),
    ("edit-name", LAUNCH, "--edit's name forgotten", "\t\t\tEditName = editName,\n", "\t\t\tEditName = null,\n"),
    ("edit-name-parse", LAUNCH, "--edit taking a name with upper-case letters",
     "!Gdpyr.Sim.Level.MapNames.TryParseName(value, out editName, out string error)",
     "!Gdpyr.Sim.Level.MapNames.TryParseName(value.ToLowerInvariant(), out editName, out string error)"),
    ("edit-mode", LAUNCH, "--edit taken beside a mode flag",
     "return Failure($\"conflicting mode flags: --edit and {other}\");", "mode = mode;"),
    ("edit-dedicated", LAUNCH, "--edit taken by a dedicated server",
     "return Failure(\"--edit needs somebody at a screen: a dedicated server has no window\");", "dedicatedServer = false;"),
    ("edit-beside", LAUNCH, "--edit taken beside --practice, --map and the rest", "if (beside != null)", "if (false)"),
    ("edit-twice", LAUNCH, "--edit given twice and taken",
     "return Failure(\"--edit given twice\");", "edit = true;"),
]


def run(command, cwd):
    result = subprocess.run(command, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    return result.stdout


def test(copy):
    """The classes, in a copy: (compiled, the tests that failed, how many ran)."""
    out = run(["dotnet", "test", "Tests/Gdpyr.Tests.csproj", "--nologo", "-v", "q", "--filter", FILTER,
               "--logger", "console;verbosity=normal"], copy)
    if re.search(r"error CS\d+|error MSB\d+", out):
        return False, [m.group(0) for m in re.finditer(r"error CS\d+: [^\[]*", out)][:1], 0
    failed = sorted({re.sub(r"\(.*", "", name).replace("Gdpyr.Tests.", "") for name in re.findall(r"^\s+Failed (\S+)", out, re.M)})
    total = re.search(r"Total:\s+(\d+)", out) or re.search(r"Total tests: (\d+)", out)
    return True, failed, int(total.group(1)) if total else 0


def make_copy(scratch, index):
    copy = os.path.join(scratch, f"tree{index}")
    skip = shutil.ignore_patterns("bin", "obj", "__pycache__", "*.uid", "*.import")
    for folder in ("Scripts", "Tests", "tools", os.path.join("ThirdParty", "FluidHTN")):
        shutil.copytree(os.path.join(ROOT, folder), os.path.join(copy, folder), ignore=skip)
    # The tests read the scenes the catalog names, Structures/*.tres, and the bundled maps, and find
    # the root by project.godot.
    for folder in ("Scenes", "Structures", "Maps"):
        shutil.copytree(os.path.join(ROOT, folder), os.path.join(copy, folder), ignore=skip)
    shutil.copy(os.path.join(ROOT, "project.godot"), copy)
    return copy


def break_rule(copy, rule):
    name, file, what, old, new = rule
    path = os.path.join(copy, file)
    with open(os.path.join(ROOT, file), encoding="utf-8") as source:
        text = source.read()
    if text.count(old) != 1:
        return name, what, None, [f"its piece of {file} is in the file {text.count(old)} times, not once"]
    with open(path, "w", encoding="utf-8") as target:
        target.write(text.replace(old, new))
    try:
        compiled, failed, _ = test(copy)
    finally:
        with open(path, "w", encoding="utf-8") as target:
            target.write(text)
    return name, what, compiled, failed


def main():
    arguments = sys.argv[1:]
    jobs = 3
    if "--jobs" in arguments:
        at = arguments.index("--jobs")
        jobs = max(1, int(arguments[at + 1]))
        del arguments[at:at + 2]
    rules = [rule for rule in RULES if not arguments or any(word in rule[0] for word in arguments)]
    if not rules:
        sys.exit(f"no rule's name holds any of: {' '.join(arguments)}")
    names = [rule[0] for rule in RULES]
    assert len(set(names)) == len(names), "two rules share a name"

    scratch = tempfile.mkdtemp(prefix="le4-mutations-")
    try:
        copies = [make_copy(scratch, index) for index in range(min(jobs, len(rules)))]
        compiled, failed, total = test(copies[0])
        if not compiled or failed or total == 0:
            print(f"FAIL the tree as it stands: {total} tests ran, and these did not pass: {', '.join(failed) or 'none'}")
            return 1
        print(f"PASS the tree as it stands: {total} of {total} tests of {', '.join(CLASSES)}")
        for copy in copies[1:]:
            test(copy)

        caught = 0
        free = list(copies)

        def work(rule):
            copy = free.pop()
            try:
                return break_rule(copy, rule)
            finally:
                free.append(copy)

        with concurrent.futures.ThreadPoolExecutor(max_workers=len(copies)) as pool:
            for name, what, compiled, failed in pool.map(work, rules):
                if compiled is None:
                    print(f"FAIL {name}: {failed[0]}")
                elif not compiled:
                    print(f"FAIL {name}: {what} -- does not compile: {failed[0] if failed else '?'}")
                elif not failed:
                    print(f"FAIL {name}: {what} -- and every test still passed")
                else:
                    caught += 1
                    shown = ", ".join(failed[:4]) + (f", and {len(failed) - 4} more" if len(failed) > 4 else "")
                    print(f"PASS {name}: {what} -- {len(failed)} fail: {shown}")
                sys.stdout.flush()
        print(f"\n{caught} of {len(rules)} broken rules were caught by a test")
        return 0 if caught == len(rules) else 1
    finally:
        shutil.rmtree(scratch, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
