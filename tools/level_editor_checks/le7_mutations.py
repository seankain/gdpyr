#!/usr/bin/env python3
"""LE7's engine-free rules, each held by a test that fails when the rule is broken: a map's chunks and
their index; the sender's requests, its once a connection, its bytes and its rate; the receiver's
refusals -- a length no map can be, a message that is not a chunk, one past the length, out of turn or
of the wrong size, a stall, bytes that do not hash to what was named, read or validate -- and that a
refusal keeps nothing; the host's settings holding a map of its own; and a demo naming a data map by
its hash, at version 6 (docs/LEVEL_EDITOR.md §6.10, §9.1, §9.3, §9.6).

    python3 tools/level_editor_checks/le7_mutations.py              # every rule, three at a time
    python3 tools/level_editor_checks/le7_mutations.py stall hash   # the ones whose name holds a word
    python3 tools/level_editor_checks/le7_mutations.py --jobs 1

le6_mutations.py's way: it copies what the tests compile and read to a scratch directory, and there,
for each rule below, breaks the rule -- one edit to one file -- and runs the test classes LE7 added or
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
CLASSES = ("MapTransferTests", "HostSettingsTests", "DemoCodecTests")
FILTER = "|".join(f"FullyQualifiedName~Gdpyr.Tests.{name}" for name in CLASSES)

TRANSFER = "Scripts/Sim/Level/MapTransfer.cs"
HOST = "Scripts/Match/HostSettings.cs"
DEMO = "Scripts/Sim/Demo/DemoFormat.cs"

# (name, file, the rule as broken, the source as it stands, what it is replaced with)
RULES = [
    # ---- the chunks (§6.10) ------------------------------------------------------------------------------
    ("chunk-size", TRANSFER, "a chunk of 1,200 B, two datagrams with its header",
     "public const int ChunkBytes = 1024;", "public const int ChunkBytes = 1200;"),
    ("chunk-count-floor", TRANSFER, "the chunks a file is sent in counted without its remainder",
     "length <= 0 ? 0 : ((length - 1) / ChunkBytes) + 1;", "length <= 0 ? 0 : length / ChunkBytes;"),
    ("chunk-last-whole", TRANSFER, "the last chunk taken for a whole one",
     "return index < count - 1 ? ChunkBytes : length - ((count - 1) * ChunkBytes);", "return ChunkBytes;"),
    ("packet-index-endian", TRANSFER, "a chunk's index written big-endian",
     "BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)index);", "BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)index);"),
    ("packet-offset", TRANSFER, "a chunk's bytes taken from the start of the file",
     "file.Slice(index * ChunkBytes, length).CopyTo", "file.Slice(0, length).CopyTo"),
    ("rate-per-tick", TRANSFER, "eight chunks a tick across peers, over 256 KB/s",
     "public const int MaxChunksPerTick = 4;", "public const int MaxChunksPerTick = 8;"),
    ("stall-time", TRANSFER, "a stream refused after 5 s with nothing",
     "public const double StallSeconds = 10.0;", "public const double StallSeconds = 5.0;"),
    ("hash-upper", TRANSFER, "a hash in upper case taken for one",
     "if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))", "if (!Uri.IsHexDigit(c))"),
    ("hash-length", TRANSFER, "a hash of any length taken",
     "if (text == null || text.Length != MapNames.HashHexLength)", "if (text == null || text.Length == 0)"),
    ("progress-line", TRANSFER, "the status line without the chunks expected",
     "$\"fetching '{name}' {received} / {chunks}\";", "$\"fetching '{name}' {received}\";"),

    # ---- the sender ----------------------------------------------------------------------------------------
    ("sender-any-map", TRANSFER, "a request for another map's hash sent this one",
     "if (!string.Equals(hash, Hash, StringComparison.Ordinal))\n\t\t{\n\t\t\treturn MapRequestAnswer.NotThisMap;",
     "if (hash == null)\n\t\t{\n\t\t\treturn MapRequestAnswer.NotThisMap;"),
    ("sender-twice", TRANSFER, "a peer that asked before sent the map again",
     "if (!_asked.Add(peer))\n\t\t{\n\t\t\treturn MapRequestAnswer.AlreadyAsked;",
     "if (!_asked.Add(peer) && _turns.Contains(peer))\n\t\t{\n\t\t\treturn MapRequestAnswer.AlreadyAsked;"),
    ("sender-forget", TRANSFER, "a connection gone, and what it asked kept",
     "\t\t_asked.Remove(peer);\n", ""),
    ("sender-forget-turn", TRANSFER, "a peer gone still sent chunks",
     "\t\t_turns.Remove(peer);\n", ""),
    ("sender-copy", TRANSFER, "the sender sending from the array it was given, not a copy",
     "_file = (byte[])file.Clone();", "_file = file;"),
    ("sender-rotate", TRANSFER, "a peer kept at the front of the line, served every tick",
     "\t\t\tif (index + 1 < Chunks)\n\t\t\t{\n\t\t\t\t_turns.Add(peer);\n\t\t\t}",
     "\t\t\tif (index + 1 < Chunks)\n\t\t\t{\n\t\t\t\t_turns.Insert(0, peer);\n\t\t\t}"),
    ("sender-cap", TRANSFER, "every peer fetching served each tick, however many",
     "int serve = Math.Min(_turns.Count, MapTransfer.MaxChunksPerTick);", "int serve = _turns.Count;"),
    ("sender-sent", TRANSFER, "what a peer was sent not counted",
     "\t\t\t_sent[peer] = index + 1;\n", ""),

    # ---- the receiver --------------------------------------------------------------------------------------
    ("receive-long", TRANSFER, "a map over 1 MiB fetched",
     "if (length > MapLimits.MaxFileBytes)", "if (length > MapLimits.MaxFileBytes * 2)"),
    ("receive-empty", TRANSFER, "a map of no bytes fetched",
     "if (length <= 0)\n\t\t{\n\t\t\terror = $\"the host's map is {length} bytes long\";",
     "if (length < 0)\n\t\t{\n\t\t\terror = $\"the host's map is {length} bytes long\";"),
    ("receive-hash-named", TRANSFER, "a fetch begun for a name that is not a hash",
     "if (!MapTransfer.IsHash(hash))\n\t\t{\n\t\t\terror = \"the host named its map by something that is not a hash\";",
     "if (hash == null)\n\t\t{\n\t\t\terror = \"the host named its map by something that is not a hash\";"),
    ("receive-short-message", TRANSFER, "a message of four bytes or fewer taken for a chunk",
     "if (packet.Length <= MapTransfer.IndexBytes || packet.Length > MapTransfer.MaxPacketBytes)",
     "if (packet.Length < MapTransfer.IndexBytes || packet.Length > MapTransfer.MaxPacketBytes)"),
    ("receive-long-message", TRANSFER, "a message longer than a chunk's taken",
     "if (packet.Length <= MapTransfer.IndexBytes || packet.Length > MapTransfer.MaxPacketBytes)",
     "if (packet.Length <= MapTransfer.IndexBytes)"),
    ("receive-past-length", TRANSFER, "a chunk past the length let through to the turn's check",
     "if (index >= (uint)Chunks)\n\t\t{\n\t\t\treturn Refuse($\"chunk {index} is past the map's {Chunks}\");\n\t\t}\n",
     ""),
    ("receive-turn", TRANSFER, "a chunk taken out of turn, written where its index says",
     "if (index != (uint)Received)", "if (index < (uint)Received)"),
    ("receive-size", TRANSFER, "a chunk of the wrong size taken",
     "if (data.Length != expected)", "if (data.Length > expected)"),
    ("receive-stall-reset", TRANSFER, "a chunk not starting the stall's clock again",
     "\t\tReceived++;\n\t\t_quiet = 0;\n", "\t\tReceived++;\n"),
    ("receive-stall-never", TRANSFER, "a stream that stalls never refused",
     "return _quiet >= MapTransfer.StallSeconds", "return _quiet >= double.MaxValue"),
    ("receive-nan", TRANSFER, "time that is not a number counted",
     "if (State != MapReceiveState.Receiving || !(seconds > 0))", "if (State != MapReceiveState.Receiving || seconds == 0)"),
    ("receive-unhashed", TRANSFER, "a whole stream taken whatever it hashes to",
     "if (!string.Equals(hash, Hash, StringComparison.Ordinal))\n\t\t{\n\t\t\treturn Refuse(",
     "if (hash == null)\n\t\t{\n\t\t\treturn Refuse("),
    ("receive-unread", TRANSFER, "bytes that are not a map taken",
     "if (!MapFile.TryRead(_buffer, out MapDocument document, out string error))\n\t\t{\n\t\t\treturn Refuse($\"the host's map does not read: {error}\");\n\t\t}",
     "MapFile.TryRead(_buffer, out MapDocument document, out string error);"),
    ("receive-unvalidated", TRANSFER, "a map this build cannot play taken",
     "if (!report.IsPlayable)\n\t\t{\n\t\t\tvar reasons", "if (false)\n\t\t{\n\t\t\tvar reasons"),
    ("receive-refusal-final", TRANSFER, "chunks taken after a refusal",
     "if (State != MapReceiveState.Receiving)\n\t\t{\n\t\t\treturn State;\n\t\t}\n\n\t\tif (packet.Length",
     "if (State == MapReceiveState.Complete)\n\t\t{\n\t\t\treturn State;\n\t\t}\n\n\t\tif (packet.Length"),
    ("receive-refusal-keeps", TRANSFER, "a refused stream's bytes kept",
     "\t\t_buffer = null;\n\t\tFile = null;\n\t\tDocument = null;", "\t\tFile = _buffer;\n\t\tDocument = null;"),

    # ---- the host's map and a demo's (§9.1, §9.6) -------------------------------------------------------------
    ("host-own-map", HOST, "a map of one's own replaced by the catalog's default",
     "Map = map.IsValid ? map : MapCatalog.Default;", "Map = map.IsValid && !string.IsNullOrEmpty(map.Id) ? map : MapCatalog.Default;"),
    ("host-invalid", HOST, "a map that is not one held",
     "Map = map.IsValid ? map : MapCatalog.Default;", "Map = map;"),
    ("host-describe", HOST, "the settings' line saying nothing of a map that is not the catalog's",
     "$\"map {(string.IsNullOrEmpty(MapId) ? Map.ToString() : MapId)} | {RoundMinutes} min",
     "$\"map {MapId} | {RoundMinutes} min"),
    ("demo-version", DEMO, "the format left at version 5",
     "public const byte Version = 6;", "public const byte Version = 5;"),
    ("demo-name-scene", DEMO, "a data map named by its scene, MapHost.tscn",
     "isData ? DataMapPrefix + (hash ?? string.Empty) : scenePath ?? string.Empty;", "scenePath ?? string.Empty;"),
    ("demo-parse-hash", DEMO, "anything after map: taken for a hash",
     "if (!Level.MapTransfer.IsHash(rest))", "if (rest.Length == 0)"),
    ("demo-parse-prefix", DEMO, "the prefix matched in any case",
     "if (map == null || !map.StartsWith(DataMapPrefix, StringComparison.Ordinal))",
     "if (map == null || !map.StartsWith(DataMapPrefix, StringComparison.OrdinalIgnoreCase))"),
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

    scratch = tempfile.mkdtemp(prefix="le7-mutations-")
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
