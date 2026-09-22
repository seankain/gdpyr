using System;
using System.Collections.Generic;
using System.IO;
using Gdpyr.Sim;
using Gdpyr.Sim.Demo;

namespace Gdpyr.Net;

/// <summary>
/// A demo being played back (docs/DEMOS.md §5).
///
/// It is a socket that happens to be a file: one call per physics step hands the
/// caller the records of the next recorded tick, and <see cref="PlayerManager"/>
/// applies them the way it applies a packet. Nothing here touches the scene tree,
/// so what a demo *means* stays in one place and what it *looks like* stays in
/// another.
///
/// Speed and pause live here rather than in the caller because they are properties
/// of the read head: pausing a demo is not advancing the file, and it must not be
/// the tree's pause — the render frame has to keep running or there is nothing to
/// look at.
/// </summary>
public sealed class DemoPlayback : IDisposable
{
	/// <summary>Fastest fast-forward. Ten minutes of round in one at 60 Hz.</summary>
	public const int MaxSpeed = 8;

	private readonly DemoReader _reader;
	private readonly List<DemoRecord> _records = new();

	private int _speed = 1;
	private uint _keyframeTick;
	private bool _hasKeyframe;

	private DemoPlayback(DemoReader reader, string fileName, string path)
	{
		_reader = reader;
		FileName = fileName;
		Path = path;
		Tick = reader.Header.StartTick;
	}

	public string FileName { get; }

	public string Path { get; }

	public DemoHeader Header => _reader.Header;

	/// <summary>The recorded tick last applied. Starts at the header's first tick.</summary>
	public uint Tick { get; private set; }

	/// <summary>Recorded ticks applied so far.</summary>
	public int TicksPlayed { get; private set; }

	/// <summary>True once the file is out of records, cleanly or not.</summary>
	public bool Ended { get; private set; }

	/// <summary>Why the file ended early, or null when it ended on its <c>End</c> tag.</summary>
	public string Error => _reader.Error;

	public bool Paused { get; set; }

	/// <summary>
	/// Recorded ticks applied per physics step. 1 is the speed it was played at;
	/// anything higher skips nothing, it just gets through the file faster, which is
	/// what keeps a fast-forwarded demo's state right rather than approximately
	/// right.
	/// </summary>
	public int Speed
	{
		get => _speed;
		set => _speed = Math.Clamp(value, 1, MaxSpeed);
	}

	/// <summary>
	/// Whether the characters in this demo are advanced from the journal rather
	/// than interpolated between keyframes. True for a
	/// <see cref="DemoKind.Journal"/> demo, which is the only kind that has a
	/// record of what everyone pressed.
	/// </summary>
	public bool Simulated => Header.Kind == DemoKind.Journal;

	/// <summary>
	/// Where interpolated entities are drawn: the same distance behind the newest
	/// keyframe that a live client draws remote players behind its server-tick
	/// estimate, so there are always two keyframes bracketing it
	/// (docs/NETCODE.md §2).
	///
	/// Measured against the keyframes rather than against the block index, because
	/// the two are the same clock only in a journal. A client's blocks are stamped
	/// with its own estimate of the server's tick and the snapshots in them are from
	/// half a round trip earlier (docs/DEMOS.md §5.2); drawing a stream demo at the
	/// block index would put the render clock ahead of every snapshot in the file.
	/// </summary>
	public float RenderTick => (_hasKeyframe ? _keyframeTick : Tick) - (float)SimConfig.InterpolationDelayTicks;

	/// <summary>
	/// Told what tick the newest applied keyframe was about. Called by whoever
	/// decodes one, because the tick is inside the payload and this does not decode
	/// payloads.
	/// </summary>
	public void NoteKeyframe(uint tick)
	{
		if (!_hasKeyframe || tick > _keyframeTick)
		{
			_keyframeTick = tick;
			_hasKeyframe = true;
		}
	}

	/// <summary>Seconds of round played so far, at the simulation's fixed rate.</summary>
	public float Seconds => Tick >= Header.StartTick
		? (Tick - Header.StartTick) / (float)SimConfig.TickRate
		: 0f;

	/// <summary>
	/// Opens a demo by the name somebody typed. <paramref name="error"/> says why
	/// when this returns null — no such file, not a demo, or a demo this build
	/// cannot read (<see cref="DemoHeader.IsPlayable"/>).
	/// </summary>
	public static DemoPlayback TryStart(string name, out string error)
	{
		string path = DemoFiles.Resolve(name, out string fileName, out error);
		if (path == null)
		{
			return null;
		}

		if (!File.Exists(path))
		{
			error = $"no demo called '{fileName}'";
			return null;
		}

		Stream stream = DemoFiles.TryOpen(path, out error);
		if (stream == null)
		{
			return null;
		}

		if (!DemoReader.TryOpen(stream, out DemoReader reader, out error))
		{
			stream.Dispose();
			return null;
		}

		return new DemoPlayback(reader, fileName, path);
	}

	/// <summary>
	/// The records of the next recorded tick, in the order they were written.
	/// False at the end of the file, which is also when <see cref="Ended"/> is set.
	///
	/// The list is this object's and is reused: a caller applies it and lets go of
	/// it, which is the same contract the snapshot scratch buffers have.
	/// </summary>
	public bool TryAdvance(out IReadOnlyList<DemoRecord> records)
	{
		records = _records;

		if (Ended || !_reader.TryReadTick(_records, out uint tick))
		{
			Ended = true;
			return false;
		}

		Tick = tick;
		TicksPlayed++;
		return true;
	}

	public void Dispose() => _reader.Dispose();

	public override string ToString() =>
		$"{FileName} | {Header.Kind} | tick {Tick} ({Seconds:0.0}s)"
		+ (Speed > 1 ? $" | x{Speed}" : string.Empty)
		+ (Paused ? " | paused" : string.Empty)
		+ (Ended ? " | ended" : string.Empty);
}
