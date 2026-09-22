using System;
using System.IO;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Sim;
using Gdpyr.Sim.Demo;
using Godot;

namespace Gdpyr.Net;

/// <summary>
/// A recording in progress (docs/DEMOS.md §4).
///
/// Everything it does is a forward to <see cref="DemoWriter"/>; what it adds is
/// the file, the header built out of this process's own situation, and the rule
/// that a recording never takes the round down with it — a failed write stops the
/// recording, says so once, and is otherwise ignored.
///
/// What it records depends on what this process is. An authority records the
/// <see cref="InputFrame"/> it resolved for every character, which is the whole
/// of what the simulation reads for that tick and therefore the whole of what a
/// replay needs (<see cref="DemoKind.Journal"/>). A client has no such thing —
/// it is never told what anybody else pressed — so it records its own input and
/// the snapshots it was sent (<see cref="DemoKind.Stream"/>).
/// </summary>
public sealed class DemoRecorder : IDisposable
{
	private readonly DemoWriter _writer;
	private bool _warned;

	private DemoRecorder(DemoWriter writer, string fileName, string path, DemoKind kind, uint startTick)
	{
		_writer = writer;
		FileName = fileName;
		Path = path;
		Kind = kind;
		StartTick = startTick;
	}

	public string FileName { get; }

	public string Path { get; }

	public DemoKind Kind { get; }

	public uint StartTick { get; }

	/// <summary>The last tick anything was written for.</summary>
	public uint LastTick => _writer.CurrentTick;

	/// <summary>Ticks covered so far. A tick nothing happened on is not one of them.</summary>
	public int TickCount => _writer.TickCount;

	public long Bytes => _writer.BytesWritten;

	/// <summary>Seconds of round in the file, at the simulation's fixed rate.</summary>
	public float Seconds => LastTick >= StartTick ? (LastTick - StartTick) / (float)SimConfig.TickRate : 0f;

	public bool IsWriting => _writer.IsWriting;

	/// <summary>Why the recording stopped, or null. A full disk, usually.</summary>
	public string Failed => _writer.Failed;

	/// <summary>
	/// Opens a demo and writes its header. <paramref name="error"/> says why when
	/// this returns null: a name that is not a file name, or a directory that
	/// cannot be written.
	/// </summary>
	public static DemoRecorder TryStart(string name, DemoKind kind, uint startTick, int localPeerId,
		out string error)
	{
		string path = DemoFiles.Resolve(name, out string fileName, out error);
		if (path == null)
		{
			return null;
		}

		Stream stream = DemoFiles.TryCreate(path, out error);
		if (stream == null)
		{
			return null;
		}

		DemoHeader header = DemoHeader.Create(kind, startTick, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
			CombatManager.Instance?.RoundSeed ?? 0, localPeerId, Session.WorldScenePath, DescribeRecorder());

		var writer = new DemoWriter(stream, header);
		if (writer.Failed != null)
		{
			error = writer.Failed;
			writer.Dispose();
			return null;
		}

		return new DemoRecorder(writer, fileName, path, kind, startTick);
	}

	public void Input(uint tick, int peerId, in InputFrame frame)
	{
		_writer.WriteInput(tick, peerId, frame);
		Warn();
	}

	public void Spawn(uint tick, int peerId, int spawnIndex, bool isBot)
	{
		_writer.WriteSpawn(tick, peerId, spawnIndex, isBot);
		Warn();
	}

	public void Despawn(uint tick, int peerId)
	{
		_writer.WriteDespawn(tick, peerId);
		Warn();
	}

	public void PlayerSnapshot(uint tick, ReadOnlySpan<byte> payload)
	{
		_writer.WritePlayerSnapshot(tick, payload);
		Warn();
	}

	public void UnitSnapshot(uint tick, ReadOnlySpan<byte> payload)
	{
		_writer.WriteUnitSnapshot(tick, payload);
		Warn();
	}

	public void Mark(uint tick, string text)
	{
		_writer.WriteMark(tick, text);
		Warn();
	}

	/// <summary>Closes the file. Safe to call twice; the second one does nothing.</summary>
	public void Stop() => Dispose();

	public void Dispose() => _writer.Dispose();

	public override string ToString() =>
		$"{FileName} | {Kind} | {TickCount} ticks ({Seconds:0.0}s) | {FormatBytes(Bytes)}";

	/// <summary>Bytes in something a person reads, matching <c>NetStats.FormatRate</c>'s shape.</summary>
	public static string FormatBytes(long bytes) => bytes switch
	{
		< 1024 => $"{bytes} B",
		< 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
		_ => $"{bytes / (1024.0 * 1024.0):0.00} MB",
	};

	/// <summary>
	/// What this process calls itself in the file. The same name it would put in a
	/// LAN browser, so a demo off somebody else's machine says whose it was
	/// (docs/LAN.md §4).
	/// </summary>
	private static string DescribeRecorder() =>
		Bootstrap.Options?.ServerName ?? ServerAdvertiser.DefaultName();

	private void Warn()
	{
		if (_warned || _writer.Failed == null)
		{
			return;
		}

		_warned = true;
		GD.PushWarning($"[demo] recording to {FileName} stopped: {_writer.Failed}");
	}
}
