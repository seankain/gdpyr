using System;

namespace Gdpyr.Sim.Demo;

/// <summary>What a demo file holds, and therefore what playing it back can do.</summary>
public enum DemoKind : byte
{
	/// <summary>
	/// Recorded by the authority: every character's resolved
	/// <see cref="InputFrame"/>, tick by tick, plus the snapshots the authority
	/// broadcast. This is the journal Quake III's <c>com_journal</c> is named after
	/// and the only kind playback can re-simulate — a tick's inputs are the whole
	/// of what the simulation reads, so replaying them replays the round.
	/// </summary>
	Journal = 1,

	/// <summary>
	/// Recorded by a client: its own input, and the snapshots it was sent. A client
	/// is never told what anybody else pressed (docs/NETCODE.md §1), so this cannot
	/// be re-simulated and is played back the way it was received — by interpolating
	/// the snapshots, exactly as the live client did.
	/// </summary>
	Stream = 2,
}

/// <summary>
/// The tag on the front of every record in the stream. A demo is a flat sequence
/// of these, in the order the recorder saw them.
/// </summary>
public enum DemoRecordKind : byte
{
	/// <summary>The stream is over. Written by <see cref="DemoWriter.Finish"/>.</summary>
	End = 0,

	/// <summary>
	/// Opens a block. Every record after it belongs to this tick, until the next
	/// one. Written lazily — only when the tick a write names differs from the last
	/// — so a tick nothing happened on costs nothing.
	/// </summary>
	Tick = 1,

	/// <summary>One character's intent for this tick, as the authority resolved it.</summary>
	Input = 2,

	/// <summary>A player joined the roster.</summary>
	Spawn = 3,

	/// <summary>A player left it.</summary>
	Despawn = 4,

	/// <summary>A <see cref="SnapshotCodec"/> payload, verbatim.</summary>
	PlayerSnapshot = 5,

	/// <summary>A <see cref="UnitSnapshotCodec"/> payload, verbatim.</summary>
	UnitSnapshot = 6,

	/// <summary>A line of text somebody asked to be put in the file at this tick.</summary>
	Mark = 7,
}

/// <summary>
/// The demo container: a header, then a flat stream of tagged records
/// (docs/DEMOS.md §2).
///
/// It is deliberately the dullest format that could work. Records are tagged and
/// length-delimited rather than self-describing, every multi-byte field is little
/// endian, and the two payload kinds are the snapshot codecs' own bytes copied
/// verbatim — so a demo costs no second encoding of anything and a codec change is
/// a version bump here rather than a translation layer.
///
/// Engine-free, like every other codec in this directory: a demo is written and
/// read through a <see cref="System.IO.Stream"/>, and what opens that stream is
/// somebody else's problem.
/// </summary>
public static class DemoFormat
{
	/// <summary>The default extension. A name that already has one keeps it.</summary>
	public const string Extension = ".dem";

	/// <summary>Where demos are written and looked for, as a Godot user path.</summary>
	public const string Directory = "user://demos";

	public const int MagicBytes = 8;

	/// <summary>ASCII <c>GDPYRDEM</c>. First eight bytes of every file.</summary>
	public static ReadOnlySpan<byte> Magic => "GDPYRDEM"u8;

	/// <summary>
	/// Bumped whenever a record's layout changes, or whenever one of the snapshot
	/// codecs the payloads are copied from does. A reader refuses anything else
	/// rather than guessing: half a decoded round is worse than a refusal.
	/// </summary>
	public const byte Version = 1;

	/// <summary>
	/// Ceiling on one recorded payload. Comfortably over the largest thing either
	/// snapshot codec produces (<see cref="UnitSnapshotCodec.MaxPayloadBytes"/> is
	/// 837), and the bound on what a corrupt length field can ask a reader to
	/// allocate.
	/// </summary>
	public const int MaxPayloadBytes = 4096;

	/// <summary>Ceiling on a header string or a mark, in UTF-8 bytes.</summary>
	public const int MaxTextBytes = 255;

	/// <summary>Whether the first bytes of a file are a demo's.</summary>
	public static bool HasMagic(ReadOnlySpan<byte> bytes) =>
		bytes.Length >= MagicBytes && bytes[..MagicBytes].SequenceEqual(Magic);

	/// <summary>Longest name <see cref="TryParseName"/> will accept, extension included.</summary>
	public const int MaxNameBytes = 64;

	/// <summary>
	/// Turns what somebody typed after <c>record</c> into a file name
	/// (docs/DEMOS.md §3).
	///
	/// The console is a text box that names a file the process then writes, so this
	/// is a trust boundary and is treated as one: a name is a single path component
	/// and nothing else. No separator, no drive, no <c>..</c>, no control
	/// character, no leading dot. <c>record game.demo</c> keeps the extension it was
	/// given — it is the name the player chose — and a name with none gets
	/// <see cref="Extension"/>.
	///
	/// Engine-free so the rules are a unit test rather than a thing to try in a
	/// running game.
	/// </summary>
	public static bool TryParseName(string name, out string fileName, out string error)
	{
		fileName = null;
		error = null;

		name = name?.Trim();
		if (string.IsNullOrEmpty(name))
		{
			error = "a demo needs a name";
			return false;
		}

		foreach (char c in name)
		{
			if (c is '/' or '\\' or ':')
			{
				error = "a demo name is a file name, not a path";
				return false;
			}

			if (char.IsControl(c) || c is '"' or '*' or '?' or '<' or '>' or '|')
			{
				error = $"'{c}' is not allowed in a demo name";
				return false;
			}
		}

		// "..", "." and anything else that starts with one: a hidden file is not what
		// somebody typing a name meant, and the two dot entries are directories.
		if (name[0] == '.')
		{
			error = "a demo name may not start with '.'";
			return false;
		}

		if (!name.Contains('.'))
		{
			name += Extension;
		}

		if (System.Text.Encoding.UTF8.GetByteCount(name) > MaxNameBytes)
		{
			error = $"a demo name is at most {MaxNameBytes} bytes";
			return false;
		}

		fileName = name;
		return true;
	}
}
