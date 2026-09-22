using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Gdpyr.Sim.Demo;

/// <summary>
/// Writes a demo (docs/DEMOS.md §2). Engine-free: it is handed a
/// <see cref="Stream"/> and never learns where that stream came from, which is
/// what lets the whole format be tested over a <c>MemoryStream</c> with no Godot
/// install and no file system.
///
/// Tick markers are written lazily. A caller stamps every record with the tick it
/// belongs to and this notices when that number changes, so a tick nothing
/// happened on costs nothing, and a record written between two physics steps —
/// a peer joining on the idle frame, say — lands in the block of the tick that
/// had just finished, which is the tick it took effect on.
///
/// Blocks never go backwards: a record stamped with a tick below the one being
/// written lands in the current block instead of opening an earlier one. On the
/// authority this never fires, because there is one clock there and it counts up.
/// On a client there are three — its input clock runs ahead of its estimate of the
/// server's, and the snapshots it is recording are from a tick that has already
/// happened (docs/NETCODE.md §2) — so without this a stream demo would be a file
/// whose blocks argue with each other. The tick a *payload* is about is inside the
/// payload and is untouched; the block index is the order things were seen in, and
/// that is all playback asks of it (docs/DEMOS.md §2.2).
///
/// Every write is a no-op once <see cref="Failed"/> is set. A full disk mid-round
/// must not take the round down with it: the recording stops, the reason is kept
/// for whoever asks, and the simulation carries on.
/// </summary>
public sealed class DemoWriter : IDisposable
{
	/// <summary>Big enough for the largest record: a tag, a length and a payload.</summary>
	private readonly byte[] _scratch = new byte[3 + DemoFormat.MaxPayloadBytes];

	private readonly Stream _stream;
	private readonly bool _leaveOpen;

	private uint _tick;
	private bool _hasTick;
	private bool _finished;

	public DemoWriter(Stream stream, in DemoHeader header, bool leaveOpen = false)
	{
		_stream = stream ?? throw new ArgumentNullException(nameof(stream));
		_leaveOpen = leaveOpen;
		Header = header;

		Write(header.Encode());
	}

	public DemoHeader Header { get; }

	/// <summary>Bytes handed to the stream, header included.</summary>
	public long BytesWritten { get; private set; }

	/// <summary>Records written, not counting tick markers.</summary>
	public int RecordCount { get; private set; }

	/// <summary>Tick markers written: how many ticks this demo covers.</summary>
	public int TickCount { get; private set; }

	/// <summary>The last tick a record was written for. 0 before the first one.</summary>
	public uint CurrentTick => _tick;

	/// <summary>Why writing stopped, or null while it has not. Set once and never cleared.</summary>
	public string Failed { get; private set; }

	public bool IsWriting => Failed == null && !_finished;

	public void WriteInput(uint tick, int peerId, in InputFrame frame)
	{
		if (!Begin(tick, DemoRecordKind.Input, 4 + InputFrame.SizeBytes, out Span<byte> body))
		{
			return;
		}

		BinaryPrimitives.WriteInt32LittleEndian(body, peerId);
		InputCodec.WriteFrame(frame, body[4..]);
		Commit(1 + 4 + InputFrame.SizeBytes);
	}

	public void WriteSpawn(uint tick, int peerId, int spawnIndex, bool isBot)
	{
		if (!Begin(tick, DemoRecordKind.Spawn, 7, out Span<byte> body))
		{
			return;
		}

		BinaryPrimitives.WriteInt32LittleEndian(body, peerId);
		BinaryPrimitives.WriteUInt16LittleEndian(body[4..], (ushort)Math.Clamp(spawnIndex, 0, ushort.MaxValue));
		body[6] = isBot ? (byte)1 : (byte)0;
		Commit(1 + 7);
	}

	public void WriteDespawn(uint tick, int peerId)
	{
		if (!Begin(tick, DemoRecordKind.Despawn, 4, out Span<byte> body))
		{
			return;
		}

		BinaryPrimitives.WriteInt32LittleEndian(body, peerId);
		Commit(1 + 4);
	}

	public void WritePlayerSnapshot(uint tick, ReadOnlySpan<byte> payload) =>
		WritePayload(tick, DemoRecordKind.PlayerSnapshot, payload);

	public void WriteUnitSnapshot(uint tick, ReadOnlySpan<byte> payload) =>
		WritePayload(tick, DemoRecordKind.UnitSnapshot, payload);

	/// <summary>
	/// Puts a line of text in the stream at this tick. Nothing in the simulation
	/// reads one back; it is there so that "this is where it went wrong" survives
	/// into the file rather than into a message somewhere else.
	/// </summary>
	public void WriteMark(uint tick, string text)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
		if (bytes.Length > DemoFormat.MaxTextBytes)
		{
			bytes = bytes.AsSpan(0, DemoFormat.MaxTextBytes).ToArray();
		}

		if (!Begin(tick, DemoRecordKind.Mark, 1 + bytes.Length, out Span<byte> body))
		{
			return;
		}

		body[0] = (byte)bytes.Length;
		bytes.CopyTo(body[1..]);
		Commit(1 + 1 + bytes.Length);
	}

	/// <summary>
	/// Closes the stream out: an <see cref="DemoRecordKind.End"/> tag and a flush.
	/// A demo that was cut off instead — the process was killed mid-round — is
	/// still readable, because the reader treats running out of bytes as the end;
	/// the tag is what tells it apart from a file that was truncated.
	/// </summary>
	public void Finish()
	{
		if (_finished || Failed != null)
		{
			return;
		}

		_scratch[0] = (byte)DemoRecordKind.End;
		Write(_scratch.AsSpan(0, 1));
		_finished = true;

		try
		{
			_stream.Flush();
		}
		catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException)
		{
			Failed = e.Message;
		}
	}

	public void Dispose()
	{
		Finish();

		if (!_leaveOpen)
		{
			_stream.Dispose();
		}
	}

	private void WritePayload(uint tick, DemoRecordKind kind, ReadOnlySpan<byte> payload)
	{
		if (payload.Length > DemoFormat.MaxPayloadBytes)
		{
			// Refused rather than truncated: half a snapshot decodes to nothing, and
			// silently dropping the tail is how a demo becomes wrong instead of short.
			Failed ??= $"{kind} of {payload.Length} B exceeds the {DemoFormat.MaxPayloadBytes} B record ceiling";
			return;
		}

		if (!Begin(tick, kind, 2 + payload.Length, out Span<byte> body))
		{
			return;
		}

		BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)payload.Length);
		payload.CopyTo(body[2..]);
		Commit(1 + 2 + payload.Length);
	}

	/// <summary>
	/// Emits the tick marker if this record opens a new block, then lays the tag
	/// into the scratch buffer and hands back the room for the body. False means
	/// the write is off — either it already failed, or it is finished.
	/// </summary>
	private bool Begin(uint tick, DemoRecordKind kind, int bodyBytes, out Span<byte> body)
	{
		body = default;
		if (!IsWriting)
		{
			return false;
		}

		if (_hasTick && tick < _tick)
		{
			// Out of order rather than out of range: put it in the block being written.
			tick = _tick;
		}

		if (!_hasTick || tick != _tick)
		{
			_scratch[0] = (byte)DemoRecordKind.Tick;
			BinaryPrimitives.WriteUInt32LittleEndian(_scratch.AsSpan(1), tick);
			Write(_scratch.AsSpan(0, 5));
			if (Failed != null)
			{
				return false;
			}

			_tick = tick;
			_hasTick = true;
			TickCount++;
		}

		_scratch[0] = (byte)kind;
		body = _scratch.AsSpan(1, bodyBytes);
		return true;
	}

	private void Commit(int totalBytes)
	{
		Write(_scratch.AsSpan(0, totalBytes));
		if (Failed == null)
		{
			RecordCount++;
		}
	}

	private void Write(ReadOnlySpan<byte> bytes)
	{
		if (Failed != null)
		{
			return;
		}

		try
		{
			_stream.Write(bytes);
			BytesWritten += bytes.Length;
		}
		catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException)
		{
			// A demo is a convenience. Losing the disk mid-round stops the recording
			// and nothing else.
			Failed = e.Message;
		}
	}
}
