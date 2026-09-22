using System;

namespace Gdpyr.Sim.Demo;

/// <summary>
/// One decoded record out of a demo (docs/DEMOS.md §2.2).
///
/// A tagged struct rather than a class hierarchy: a demo is read at 60 Hz by a
/// loop that switches on the tag anyway, and the widest record here is a payload
/// reference plus four numbers.
///
/// <see cref="Tick"/> is filled in by the reader from the block the record was in,
/// so a caller never has to track the last <see cref="DemoRecordKind.Tick"/>
/// marker itself.
/// </summary>
public readonly struct DemoRecord
{
	public readonly DemoRecordKind Kind;

	/// <summary>The tick this record belongs to: the block it was read out of.</summary>
	public readonly uint Tick;

	/// <summary>Set by <see cref="DemoRecordKind.Input"/>, <c>Spawn</c> and <c>Despawn</c>.</summary>
	public readonly int PeerId;

	/// <summary>Set by <see cref="DemoRecordKind.Input"/>.</summary>
	public readonly InputFrame Frame;

	/// <summary>Set by <see cref="DemoRecordKind.Spawn"/>.</summary>
	public readonly int SpawnIndex;

	/// <summary>Set by <see cref="DemoRecordKind.Spawn"/>: this player's intent comes from a brain.</summary>
	public readonly bool IsBot;

	/// <summary>
	/// The snapshot codec's own bytes, for the two snapshot records. Null for
	/// everything else. Owned by the caller — the reader hands over a fresh array
	/// rather than a window into a buffer it will reuse.
	/// </summary>
	public readonly byte[] Payload;

	/// <summary>Set by <see cref="DemoRecordKind.Mark"/>.</summary>
	public readonly string Text;

	private DemoRecord(DemoRecordKind kind, uint tick, int peerId, in InputFrame frame, int spawnIndex,
		bool isBot, byte[] payload, string text)
	{
		Kind = kind;
		Tick = tick;
		PeerId = peerId;
		Frame = frame;
		SpawnIndex = spawnIndex;
		IsBot = isBot;
		Payload = payload;
		Text = text;
	}

	public static DemoRecord Input(uint tick, int peerId, in InputFrame frame) =>
		new(DemoRecordKind.Input, tick, peerId, frame, 0, false, null, null);

	public static DemoRecord Spawn(uint tick, int peerId, int spawnIndex, bool isBot) =>
		new(DemoRecordKind.Spawn, tick, peerId, default, spawnIndex, isBot, null, null);

	public static DemoRecord Despawn(uint tick, int peerId) =>
		new(DemoRecordKind.Despawn, tick, peerId, default, 0, false, null, null);

	public static DemoRecord Snapshot(DemoRecordKind kind, uint tick, byte[] payload) =>
		new(kind, tick, 0, default, 0, false, payload ?? Array.Empty<byte>(), null);

	public static DemoRecord Mark(uint tick, string text) =>
		new(DemoRecordKind.Mark, tick, 0, default, 0, false, null, text ?? string.Empty);

	public override string ToString() => Kind switch
	{
		DemoRecordKind.Input => $"tick {Tick} input peer {PeerId} ({Frame})",
		DemoRecordKind.Spawn => $"tick {Tick} spawn peer {PeerId} at {SpawnIndex}{(IsBot ? " (bot)" : string.Empty)}",
		DemoRecordKind.Despawn => $"tick {Tick} despawn peer {PeerId}",
		DemoRecordKind.PlayerSnapshot => $"tick {Tick} player snapshot, {Payload?.Length ?? 0} B",
		DemoRecordKind.UnitSnapshot => $"tick {Tick} unit snapshot, {Payload?.Length ?? 0} B",
		DemoRecordKind.Mark => $"tick {Tick} mark '{Text}'",
		_ => $"tick {Tick} {Kind}",
	};
}
