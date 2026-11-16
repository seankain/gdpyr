#!/usr/bin/env python3
"""LE8's engine-free rules, each held by a test that fails when the rule is broken: an upload's proof --
what it is made over, what it is keyed by, how it is compared -- and what a token must be; the server's
gate -- off with no token, the proof before anything else is said, the order of its answers, one upload
at a time and from one peer, a stream that stalls, a peer that goes -- and its answer to a next-map
request; the answers' names and numbers; the client's uploader -- what it sends and when, what each
answer makes of it, what it holds the server's word to, how long it waits; maps/active, what a
supervisor is, and when a server told its next map leaves; --map-upload-token; and what the editor says
of a publish (docs/LEVEL_EDITOR.md §6.10, §9.5).

    python3 tools/level_editor_checks/le8_mutations.py              # every rule, three at a time
    python3 tools/level_editor_checks/le8_mutations.py gate proof   # the ones whose name holds a word
    python3 tools/level_editor_checks/le8_mutations.py --jobs 1

le7_mutations.py's way: it copies what the tests compile and read to a scratch directory, and there,
for each rule below, breaks the rule -- one edit to one file -- and runs the test classes LE8 added or
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
CLASSES = ("MapTransferTests", "MapUploadTests", "MapLaunchTests", "MapEditorControlsTests")
FILTER = "|".join(f"FullyQualifiedName~Gdpyr.Tests.{name}" for name in CLASSES)

TRANSFER = "Scripts/Sim/Level/MapTransfer.cs"
UPLOAD = "Scripts/Sim/Level/MapUpload.cs"
LAUNCH = "Scripts/Core/LaunchOptions.cs"
CONTROLS = "Scripts/MapEditor/MapEditorControls.cs"

# (name, file, the rule as broken, the source as it stands, what it is replaced with)
RULES = [
    # ---- the proof (§6.10) -----------------------------------------------------------------------------------
    ("proof-no-op", TRANSFER, "a proof made over the nonce and the hash, and not the op",
     "message[^1] = (byte)op;", "message[^1] = 0;"),
    ("proof-no-nonce", TRANSFER, "a proof made over the hash and the op, and not the connection's nonce",
     "\t\tnonce.CopyTo(message);\n", ""),
    ("proof-no-hash", TRANSFER, "a proof made over the nonce and the op, and not the map's hash",
     "\t\tConvert.FromHexString(hash).CopyTo(message[MapTransfer.NonceBytes..]);\n", ""),
    ("proof-no-key", TRANSFER, "a proof that is a hash of its message, keyed by nothing",
     "return HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), message);", "return SHA256.HashData(message);"),
    ("proof-prefix", TRANSFER, "a proof held to its first four bytes",
     "return expected != null && CryptographicOperations.FixedTimeEquals(expected, proof);",
     "return expected != null && expected.AsSpan(0, 4).SequenceEqual(proof[..4]);"),
    ("proof-empty-token", TRANSFER, "a proof made with no token, by an empty key",
     "if (string.IsNullOrEmpty(token) || nonce.Length != MapTransfer.NonceBytes || !MapTransfer.IsHash(hash))",
     "if (token == null || nonce.Length != MapTransfer.NonceBytes || !MapTransfer.IsHash(hash))"),
    ("proof-short-nonce", TRANSFER, "a proof made over a nonce shorter than a connection's",
     "if (string.IsNullOrEmpty(token) || nonce.Length != MapTransfer.NonceBytes || !MapTransfer.IsHash(hash))",
     "if (string.IsNullOrEmpty(token) || nonce.Length > MapTransfer.NonceBytes || !MapTransfer.IsHash(hash))"),
    ("token-short", TRANSFER, "a token of eight characters taken",
     "public const int MinTokenLength = 16;", "public const int MinTokenLength = 8;"),
    ("token-long", TRANSFER, "a token of any length taken",
     "if (token.Length < MinTokenLength || token.Length > MaxTokenLength)", "if (token.Length < MinTokenLength)"),
    ("token-space", TRANSFER, "a token with a space in it taken",
     "if (char.IsWhiteSpace(c) || char.IsControl(c))", "if (char.IsControl(c))"),
    ("receiver-uploader", TRANSFER, "an upload's refusal naming the host",
     "? new MapReceiver(hash, length, Uploader)", "? new MapReceiver(hash, length, Host)"),

    # ---- the gate: an offer (§9.5) ------------------------------------------------------------------------------
    ("gate-off", UPLOAD, "a server with no token answering as one with a token would",
     "if (!Enabled)\n\t\t{\n\t\t\treturn MapOfferAnswer.UploadsOff;", "if (false)\n\t\t{\n\t\t\treturn MapOfferAnswer.UploadsOff;"),
    ("gate-any-proof", UPLOAD, "an offer taken with any proof that is not empty",
     "if (!MapProof.Verify(_token, nonce, hash, MapProofOp.Upload, proof))\n\t\t{\n\t\t\treturn MapOfferAnswer.BadProof;",
     "if (proof.IsEmpty)\n\t\t{\n\t\t\treturn MapOfferAnswer.BadProof;"),
    ("gate-proof-op", UPLOAD, "an offer taken with the proof for naming the next map",
     "if (!MapProof.Verify(_token, nonce, hash, MapProofOp.Upload, proof))",
     "if (!MapProof.Verify(_token, nonce, hash, MapProofOp.SetNext, proof))"),
    ("gate-too-large", UPLOAD, "an offer of over 1 MiB taken",
     "if (length <= 0 || length > MapLimits.MaxFileBytes)\n\t\t{\n\t\t\treturn MapOfferAnswer.TooLarge;",
     "if (length <= 0)\n\t\t{\n\t\t\treturn MapOfferAnswer.TooLarge;"),
    ("gate-empty", UPLOAD, "an offer of no bytes taken",
     "if (length <= 0 || length > MapLimits.MaxFileBytes)\n\t\t{\n\t\t\treturn MapOfferAnswer.TooLarge;",
     "if (length < 0 || length > MapLimits.MaxFileBytes)\n\t\t{\n\t\t\treturn MapOfferAnswer.TooLarge;"),
    ("gate-have-before-proof", UPLOAD, "a peer without the token told whether the server has a map",
     "if (!MapProof.Verify(_token, nonce, hash, MapProofOp.Upload, proof))\n\t\t{\n\t\t\treturn MapOfferAnswer.BadProof;\n\t\t}",
     "if (has != null && has(hash))\n\t\t{\n\t\t\treturn MapOfferAnswer.HaveIt;\n\t\t}\n\n"
     "\t\tif (!MapProof.Verify(_token, nonce, hash, MapProofOp.Upload, proof))\n\t\t{\n\t\t\treturn MapOfferAnswer.BadProof;\n\t\t}"),
    ("gate-large-before-proof", UPLOAD, "a peer without the token told that its map is too long",
     "if (!MapProof.Verify(_token, nonce, hash, MapProofOp.Upload, proof))\n\t\t{\n\t\t\treturn MapOfferAnswer.BadProof;\n\t\t}",
     "if (length > MapLimits.MaxFileBytes)\n\t\t{\n\t\t\treturn MapOfferAnswer.TooLarge;\n\t\t}\n\n"
     "\t\tif (!MapProof.Verify(_token, nonce, hash, MapProofOp.Upload, proof))\n\t\t{\n\t\t\treturn MapOfferAnswer.BadProof;\n\t\t}"),
    ("gate-busy-before-have", UPLOAD, "a map the server has answered busy while it takes another",
     "if (has != null && has(hash))\n\t\t{\n\t\t\treturn MapOfferAnswer.HaveIt;\n\t\t}\n\n\t\tif (UploaderPeer != 0)\n\t\t{\n\t\t\treturn MapOfferAnswer.Busy;\n\t\t}",
     "if (UploaderPeer != 0)\n\t\t{\n\t\t\treturn MapOfferAnswer.Busy;\n\t\t}\n\n\t\tif (has != null && has(hash))\n\t\t{\n\t\t\treturn MapOfferAnswer.HaveIt;\n\t\t}"),
    ("gate-have-never", UPLOAD, "a map the server has taken again",
     "if (has != null && has(hash))\n\t\t{\n\t\t\treturn MapOfferAnswer.HaveIt;", "if (false)\n\t\t{\n\t\t\treturn MapOfferAnswer.HaveIt;"),
    ("gate-two-uploads", UPLOAD, "a second upload taken while the first is under way",
     "if (UploaderPeer != 0)\n\t\t{\n\t\t\treturn MapOfferAnswer.Busy;", "if (false)\n\t\t{\n\t\t\treturn MapOfferAnswer.Busy;"),

    # ---- the gate: the chunks ------------------------------------------------------------------------------------
    ("gate-any-peers-chunks", UPLOAD, "a chunk taken from a peer whose offer was not the one taken",
     "if (peer == 0 || peer != UploaderPeer || Receiving is not { State: MapReceiveState.Receiving })",
     "if (Receiving is not { State: MapReceiveState.Receiving })"),
    ("gate-chunks-after", UPLOAD, "a chunk taken once the upload is whole",
     "if (peer == 0 || peer != UploaderPeer || Receiving is not { State: MapReceiveState.Receiving })",
     "if (peer == 0 || peer != UploaderPeer || Receiving == null)"),
    ("gate-no-stall", UPLOAD, "an upload that stops arriving never refused",
     "public void Advance(double seconds) => Receiving?.Advance(seconds);", "public void Advance(double seconds) => _ = seconds;"),
    ("gate-release-keeps", UPLOAD, "an upload let go of, and its peer still the one uploading",
     "\t\tReceiving = null;\n\t\tUploaderPeer = 0;\n\t\treturn receiver;", "\t\tReceiving = null;\n\t\treturn receiver;"),
    ("gate-forget-any", UPLOAD, "any peer going taking the upload under way with it",
     "if (peer == 0 || peer != UploaderPeer)\n\t\t{\n\t\t\treturn false;\n\t\t}\n\n\t\tRelease(out _);",
     "if (peer == 0)\n\t\t{\n\t\t\treturn false;\n\t\t}\n\n\t\tRelease(out _);"),
    ("gate-forget-keeps", UPLOAD, "a peer gone, and its upload still being taken",
     "\t\tRelease(out _);\n\t\treturn true;", "\t\treturn true;"),

    # ---- the gate: the next map ------------------------------------------------------------------------------------
    ("next-off", UPLOAD, "a server with no token told its next map",
     "if (!Enabled)\n\t\t{\n\t\t\treturn MapNextAnswer.UploadsOff;", "if (false)\n\t\t{\n\t\t\treturn MapNextAnswer.UploadsOff;"),
    ("next-any-proof", UPLOAD, "a next map named with any proof that is not empty",
     "if (!MapProof.Verify(_token, nonce, hash, MapProofOp.SetNext, proof))\n\t\t{\n\t\t\treturn MapNextAnswer.BadProof;",
     "if (proof.IsEmpty)\n\t\t{\n\t\t\treturn MapNextAnswer.BadProof;"),
    ("next-proof-op", UPLOAD, "a next map named with an upload's proof",
     "if (!MapProof.Verify(_token, nonce, hash, MapProofOp.SetNext, proof))",
     "if (!MapProof.Verify(_token, nonce, hash, MapProofOp.Upload, proof))"),
    ("next-pinned", UPLOAD, "a server started with --map told its next map",
     "if (pinned)\n\t\t{\n\t\t\treturn MapNextAnswer.Pinned;", "if (false)\n\t\t{\n\t\t\treturn MapNextAnswer.Pinned;"),
    ("next-not-here", UPLOAD, "a map the server has not got named its next",
     "if (plays == null || !plays(hash))", "if (plays == null)"),
    ("next-playing", UPLOAD, "the map being played named next, and the server leaving for it",
     "if (string.Equals(hash, playing, StringComparison.Ordinal))\n\t\t{\n\t\t\treturn MapNextAnswer.Playing;",
     "if (false)\n\t\t{\n\t\t\treturn MapNextAnswer.Playing;"),
    ("next-stays-down", UPLOAD, "a server run by hand not saying that it stays down",
     "return supervised ? MapNextAnswer.Ok : MapNextAnswer.OkStaysDown;", "return MapNextAnswer.Ok;"),

    # ---- the answers -------------------------------------------------------------------------------------------------
    ("answer-name", UPLOAD, "an answer's name not §9.5's",
     "MapOfferAnswer.HaveIt => \"have_it\",", "MapOfferAnswer.HaveIt => \"have-it\","),
    ("answer-number", UPLOAD, "an answer's number on the wire moved",
     "\tBusy = 5,", "\tBusy = 6,"),
    ("answer-unknown", UPLOAD, "a number off the wire that is no answer taken for one",
     "return wire is >= (int)MapOfferAnswer.Ok and <= (int)MapOfferAnswer.Busy;", "return wire >= 0;"),
    ("next-answer-name", UPLOAD, "a next-map answer's name changed",
     "MapNextAnswer.OkStaysDown => \"ok_stays_down\",", "MapNextAnswer.OkStaysDown => \"ok\","),
    ("next-answer-unknown", UPLOAD, "a number off the wire that is no next-map answer taken for one",
     "return wire is >= (int)MapNextAnswer.Ok and <= (int)MapNextAnswer.NotWritten;", "return wire >= 0;"),
    ("next-accepted", UPLOAD, "the map being played not counted as taken",
     "answer is MapNextAnswer.Ok or MapNextAnswer.OkStaysDown or MapNextAnswer.Playing;",
     "answer is MapNextAnswer.Ok or MapNextAnswer.OkStaysDown;"),
    ("stays-down-said", UPLOAD, "the line for a server that stays down not saying so",
     "MapNextAnswer.OkStaysDown => \"the server stops when its round ends, and nothing there starts it again:\"\n"
     "\t\t\t+ \" it plays this map the next time somebody starts it\",",
     "MapNextAnswer.OkStaysDown => \"the server changes to it when its round ends\","),
    ("clean-control", UPLOAD, "a server's words shown with their control characters",
     "clean.Append(char.IsControl(c) ? ' ' : c);", "clean.Append(c);"),
    ("clean-length", UPLOAD, "a server's words shown at any length",
     "public const int MaxReasonLength = 240;", "public const int MaxReasonLength = 2400;"),

    # ---- the uploader ------------------------------------------------------------------------------------------------
    ("uploader-copy", UPLOAD, "the uploader sending from the array it was given, not a copy",
     "_file = (byte[])file.Clone();", "_file = file;"),
    ("uploader-unasked", UPLOAD, "chunks sent before the offer is answered",
     "if (State != MapUploadState.Sending)\n\t\t{\n\t\t\treturn null;",
     "if (State is not (MapUploadState.Sending or MapUploadState.Offered))\n\t\t{\n\t\t\treturn null;"),
    ("uploader-second-answer", UPLOAD, "a second answer changing what the first made of the upload",
     "if (State != MapUploadState.Offered)\n\t\t{\n\t\t\treturn;\n\t\t}\n\n\t\t// Answered:",
     "if (Done)\n\t\t{\n\t\t\treturn;\n\t\t}\n\n\t\t// Answered:"),
    ("uploader-have-sends", UPLOAD, "a map the server has sent all the same",
     "case MapOfferAnswer.HaveIt:\n\t\t\t\tState = MapUploadState.HaveIt;", "case MapOfferAnswer.HaveIt:\n\t\t\t\tState = MapUploadState.Sending;"),
    ("uploader-refused-sends", UPLOAD, "a map sent to a server that refused the offer",
     "default:\n\t\t\t\tRefuse(MapAnswers.Describe(answer));", "default:\n\t\t\t\tState = MapUploadState.Sending;"),
    ("uploader-unknown-sends", UPLOAD, "a map sent on an answer this build does not know",
     "Refuse(MapAnswers.Unknown(wire));\n\t\t\treturn;", "State = MapUploadState.Sending;\n\t\t\treturn;"),
    ("uploader-order", UPLOAD, "the chunks sent from the last backwards",
     "byte[] packet = MapTransfer.Packet(_file, Sent);", "byte[] packet = MapTransfer.Packet(_file, Chunks - 1 - Sent);"),
    ("uploader-kept-early", UPLOAD, "the server's word that it kept the map taken before it was all sent",
     "else if (State != MapUploadState.Sent || !string.Equals(hash, Hash, StringComparison.Ordinal))",
     "else if (!string.Equals(hash, Hash, StringComparison.Ordinal))"),
    ("uploader-kept-other", UPLOAD, "the server's word that it kept another map taken",
     "else if (State != MapUploadState.Sent || !string.Equals(hash, Hash, StringComparison.Ordinal))",
     "else if (State != MapUploadState.Sent)"),
    ("uploader-kept-reason", UPLOAD, "the server's reason for not keeping the map passed over",
     "if (!string.IsNullOrEmpty(error))\n\t\t{\n\t\t\tRefuse($\"the server did not keep it:",
     "if (false)\n\t\t{\n\t\t\tRefuse($\"the server did not keep it:"),
    ("uploader-kept-unoffered", UPLOAD, "the server's word on a map whose offer it has not answered taken",
     "if (State is not (MapUploadState.Sending or MapUploadState.Sent))\n\t\t{\n\t\t\treturn;",
     "if (Done)\n\t\t{\n\t\t\treturn;"),
    ("uploader-waits", UPLOAD, "a server that never answers waited for without end",
     "if (_quiet >= MapTransfer.StallSeconds)", "if (_quiet >= double.MaxValue)"),
    ("uploader-clock-sending", UPLOAD, "the wait counted while the chunks are going",
     "if (State is not (MapUploadState.Offered or MapUploadState.Sent) || !(seconds > 0))", "if (Done || !(seconds > 0))"),
    ("uploader-clock-carried", UPLOAD, "the wait for the offer's answer counted again after the last chunk",
     "\t\t// Answered: what it waits for next, once the last chunk is gone, is waited for from then.\n\t\t_quiet = 0;\n", ""),
    ("uploader-nan", UPLOAD, "time that is not a number, or is less than none, counted",
     "if (State is not (MapUploadState.Offered or MapUploadState.Sent) || !(seconds > 0))",
     "if (State is not (MapUploadState.Offered or MapUploadState.Sent) || seconds == 0)"),
    ("uploader-dropped", UPLOAD, "a link that drops leaving the upload as it was",
     "if (!Done)\n\t\t{\n\t\t\tRefuse($\"the link to the server dropped", "if (false)\n\t\t{\n\t\t\tRefuse($\"the link to the server dropped"),
    ("uploader-dropped-done", UPLOAD, "a link that drops undoing an upload that was over",
     "if (!Done)\n\t\t{\n\t\t\tRefuse($\"the link to the server dropped", "if (true)\n\t\t{\n\t\t\tRefuse($\"the link to the server dropped"),

    # ---- maps/active, a supervisor, and leaving -------------------------------------------------------------------
    ("active-name", UPLOAD, "the file called something else",
     "public const string ActiveFileName = \"active\";", "public const string ActiveFileName = \"next\";"),
    ("active-text", UPLOAD, "the hash written with no line's end",
     "public static string ActiveText(string hash) => hash + \"\\n\";", "public static string ActiveText(string hash) => hash;"),
    ("active-untrimmed", UPLOAD, "a hash with its line's end not read as one",
     "string trimmed = text?.Trim();", "string trimmed = text;"),
    ("active-empty", UPLOAD, "an empty file said to hold something that is not a hash",
     "if (string.IsNullOrEmpty(trimmed))\n\t\t{\n\t\t\terror = \"it is empty\";\n\t\t\treturn false;\n\t\t}\n\n", ""),
    ("active-prefix", UPLOAD, "the start of a hash, or one in upper case, taken for the map's",
     "if (!MapTransfer.IsHash(trimmed))", "if (!MapNames.IsHashPrefix(trimmed))"),
    ("supervised-systemd", UPLOAD, "systemd's unit not taken for a supervisor",
     "!string.IsNullOrWhiteSpace(invocationId) || string.Equals(supervised?.Trim(), \"1\", StringComparison.Ordinal);",
     "string.Equals(supervised?.Trim(), \"1\", StringComparison.Ordinal);"),
    ("supervised-any", UPLOAD, "GDPYR_SUPERVISED=0 taken for a supervisor",
     "string.Equals(supervised?.Trim(), \"1\", StringComparison.Ordinal);", "!string.IsNullOrWhiteSpace(supervised);"),
    ("leaves-mid-round", UPLOAD, "a server leaving at once, in the middle of a round somebody is in",
     "nextMapNamed && (!roundInProgress || peers <= 0);", "nextMapNamed;"),
    ("leaves-unnamed", UPLOAD, "a server told nothing leaving between rounds",
     "nextMapNamed && (!roundInProgress || peers <= 0);", "!roundInProgress || peers <= 0;"),
    ("leaves-empty-round", UPLOAD, "a round nobody is in waited out",
     "nextMapNamed && (!roundInProgress || peers <= 0);", "nextMapNamed && !roundInProgress;"),
    ("leaves-warmup", UPLOAD, "a server with people on it and no round waiting for one to end",
     "nextMapNamed && (!roundInProgress || peers <= 0);", "nextMapNamed && peers <= 0;"),
    ("leave-grace", UPLOAD, "the link closed the moment the server leaves",
     "public const double LeaveGraceSeconds = 1.0;", "public const double LeaveGraceSeconds = 0.0;"),

    # ---- --map-upload-token ------------------------------------------------------------------------------------------
    ("flag-any-mode", LAUNCH, "a listen host given a token",
     "if (mapUploadToken != null && mode != LaunchMode.Server)", "if (mapUploadToken != null && mode == LaunchMode.Client)"),
    ("flag-unchecked", LAUNCH, "any token taken, however short",
     "if (!Gdpyr.Sim.Level.MapProof.IsToken(value, out string error))\n\t\t\t\t\t{\n\t\t\t\t\t\treturn Failure($\"--map-upload-token: {error}\");\n\t\t\t\t\t}\n\n",
     ""),
    ("flag-printed", LAUNCH, "the token printed with the options",
     "mode += \" | map uploads (token)\";", "mode += $\" | map uploads ({MapUploadToken})\";"),
    ("flag-twice", LAUNCH, "a second token taking the first's place",
     "if (mapUploadToken != null)\n\t\t\t\t\t{\n\t\t\t\t\t\treturn Failure(\"--map-upload-token given twice\");\n\t\t\t\t\t}\n\n", ""),
    ("flag-dropped", LAUNCH, "the token parsed and not kept",
     "\t\t\tMapUploadToken = mapUploadToken,\n", ""),

    # ---- what the editor says ------------------------------------------------------------------------------------------
    ("publish-no-token", CONTROLS, "a publish begun with no token",
     "if (string.IsNullOrWhiteSpace(token))\n\t\t{\n\t\t\terror = \"the server's upload token is needed",
     "if (token == null)\n\t\t{\n\t\t\terror = \"the server's upload token is needed"),
    ("publish-any-address", CONTROLS, "a publish begun to an address that is not one",
     "if (!Gdpyr.Core.LaunchOptions.TryParseAddress(address, out host, out port, out error))\n\t\t{\n\t\t\terror = $\"the server's address: {error}\";\n\t\t\treturn false;\n\t\t}",
     "Gdpyr.Core.LaunchOptions.TryParseAddress(address, out host, out port, out error);"),
    ("publish-line", CONTROLS, "the status line without the chunks to go",
     "$\"publishing '{name}' {sent} / {chunks}\";", "$\"publishing '{name}' {sent}\";"),
    ("published-had", CONTROLS, "a map the server had said to have been kept",
     "(upload == MapUploadState.HaveIt ? \"the server has this map already\" : \"the server kept it\")", "\"the server kept it\""),
    ("published-not-next", CONTROLS, "a refused next map not said to be refused",
     "(MapAnswers.Accepted(next) ? string.Empty : \"it does not play it next: \")", "string.Empty"),
    ("published-unknown", CONTROLS, "an answer this build does not know said nothing of",
     ": MapAnswers.Unknown(wire));", ": string.Empty);"),
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

    scratch = tempfile.mkdtemp(prefix="le8-mutations-")
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
