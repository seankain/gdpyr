using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Gdpyr.Sim.Demo;

/// <summary>
/// Reads a demo back (docs/DEMOS.md §2). The counterpart to
/// <see cref="DemoWriter"/>, and engine-free for the same reason.
///
/// Total, like every other decoder here (<see cref="InputCodec"/>,
/// <see cref="SnapshotCodec"/>): a truncated, corrupt or hostile file ends the
/// stream with <see cref="Error"/> set instead of throwing. A demo comes off a
/// disk, out of a zip somebody mailed around, or off the end of a process that
/// was killed mid-round, and none of those should be able to take a client down.
///
/// Records come back one at a time, each already stamped with the tick of the
/// block it was in, so a caller never tracks the markers itself. Reading a whole
/// tick at once is <see cref="TryReadTick"/>.
/// </summary>
public sealed class DemoReader : IDisposable
{
	private readonly Stream _stream;
	private readonly bool _leaveOpen;
	private readonly byte[] _scratch = new byte[DemoFormat.MaxPayloadBytes];

	private uint _tick;
	private bool _hasTick;

	/// <summary>
	/// The first record of the next block, read while finding the end of this one.
	/// See <see cref="TryReadTick"/>.
	/// </summary>
	private DemoRecord? _pending;

	private DemoReader(Stream stream, in DemoHeader header, bool leaveOpen)
	{
		_stream = stream;
		_leaveOpen = leaveOpen;
		Header = header;
	}

	public DemoHeader Header { get; }

	/// <summary>True once the stream is over, whether cleanly or not.</summary>
	public bool Ended { get; private set; }

	/// <summary>Why the stream ended early, or null when it ended on its <c>End</c> tag.</summary>
	public string Error { get; private set; }

	/// <summary>Records handed out so far, tick markers excluded.</summary>
	public int RecordsRead { get; private set; }

	/// <summary>The tick of the block being read. 0 before the first marker.</summary>
	public uint CurrentTick => _tick;

	/// <summary>
	/// Opens a demo. <paramref name="error"/> says why when this returns false —
	/// a file that is not a demo, a format version this build does not read, or a
	/// tick rate it cannot replay (<see cref="DemoHeader.IsPlayable"/>).
	/// </summary>
	public static bool TryOpen(Stream stream, out DemoReader reader, out string error, bool leaveOpen = false)
	{
		reader = null;
		error = null;

		if (stream == null || !stream.CanRead)
		{
			error = "not a readable stream";
			return false;
		}

		// The header is variable length, so it is read in two goes: the fixed part
		// says nothing about how long the two strings are, and seeking back is not
		// something every stream can do.
		var bytes = new byte[DemoHeader.FixedBytes + 2 + (DemoFormat.MaxTextBytes * 2)];
		int read = ReadUpTo(stream, bytes, bytes.Length);

		if (read < DemoFormat.MagicBytes || !DemoFormat.HasMagic(bytes))
		{
			error = "not a gdpyr demo";
			return false;
		}

		if (!DemoHeader.TryDecode(bytes.AsSpan(0, read), out DemoHeader header, out int headerBytes))
		{
			error = "the demo header is truncated";
			return false;
		}

		if (!header.IsPlayable(out string reason))
		{
			error = reason;
			return false;
		}

		// Whatever was read past the header is the front of the record stream. Rather
		// than require a seekable stream, hand it back through a concatenation.
		Stream records = read > headerBytes
			? new PrefixStream(bytes.AsSpan(headerBytes, read - headerBytes).ToArray(), stream, leaveOpen)
			: stream;

		reader = new DemoReader(records, header, leaveOpen && ReferenceEquals(records, stream));
		return true;
	}

	/// <summary>
	/// The next record, or false at the end of the stream. A record that cannot be
	/// decoded ends the stream with <see cref="Error"/> set rather than being
	/// skipped: the stream is a sequence, and a reader that resynchronizes on a
	/// tag byte would happily read a payload's contents as records.
	/// </summary>
	public bool TryReadRecord(out DemoRecord record)
	{
		record = default;
		if (Ended)
		{
			return false;
		}

		while (true)
		{
			if (!TryReadByte(out byte tag))
			{
				// Out of bytes with no End tag: a demo the recorder never closed, which
				// is what a process killed mid-round leaves behind. Not an error.
				Ended = true;
				return false;
			}

			switch ((DemoRecordKind)tag)
			{
				case DemoRecordKind.End:
					Ended = true;
					return false;

				case DemoRecordKind.Tick:
				{
					if (!TryRead(4))
					{
						return Truncated("a tick marker");
					}

					uint tick = BinaryPrimitives.ReadUInt32LittleEndian(_scratch);
					if (_hasTick && tick < _tick)
					{
						// Blocks are written in order and read in order. One that goes
						// backwards is a file that has been spliced or corrupted, and
						// replaying it would put inputs on ticks that already happened.
						return Fail($"tick {tick} follows tick {_tick}");
					}

					_tick = tick;
					_hasTick = true;
					continue;
				}

				case DemoRecordKind.Input:
				{
					if (!TryRead(4 + InputFrame.SizeBytes))
					{
						return Truncated("an input record");
					}

					record = DemoRecord.Input(_tick, BinaryPrimitives.ReadInt32LittleEndian(_scratch),
						InputCodec.ReadFrame(_scratch.AsSpan(4)));
					RecordsRead++;
					return true;
				}

				case DemoRecordKind.Spawn:
				{
					if (!TryRead(7))
					{
						return Truncated("a spawn record");
					}

					record = DemoRecord.Spawn(_tick, BinaryPrimitives.ReadInt32LittleEndian(_scratch),
						BinaryPrimitives.ReadUInt16LittleEndian(_scratch.AsSpan(4)), _scratch[6] != 0);
					RecordsRead++;
					return true;
				}

				case DemoRecordKind.Despawn:
				{
					if (!TryRead(4))
					{
						return Truncated("a despawn record");
					}

					record = DemoRecord.Despawn(_tick, BinaryPrimitives.ReadInt32LittleEndian(_scratch));
					RecordsRead++;
					return true;
				}

				case DemoRecordKind.PlayerSnapshot:
				case DemoRecordKind.UnitSnapshot:
				{
					if (!TryRead(2))
					{
						return Truncated("a snapshot length");
					}

					int length = BinaryPrimitives.ReadUInt16LittleEndian(_scratch);
					if (length > DemoFormat.MaxPayloadBytes)
					{
						return Fail($"a {length} B payload exceeds the {DemoFormat.MaxPayloadBytes} B ceiling");
					}

					if (!TryRead(length))
					{
						return Truncated("a snapshot payload");
					}

					record = DemoRecord.Snapshot((DemoRecordKind)tag, _tick, _scratch.AsSpan(0, length).ToArray());
					RecordsRead++;
					return true;
				}

				case DemoRecordKind.Mark:
				{
					if (!TryRead(1))
					{
						return Truncated("a mark length");
					}

					int length = _scratch[0];
					if (!TryRead(length))
					{
						return Truncated("a mark");
					}

					record = DemoRecord.Mark(_tick, Encoding.UTF8.GetString(_scratch, 0, length));
					RecordsRead++;
					return true;
				}

				default:
					return Fail($"unknown record tag 0x{tag:x2}");
			}
		}
	}

	/// <summary>
	/// Every record of the next tick, in order, into <paramref name="into"/>.
	/// Returns false at the end of the stream; <paramref name="tick"/> is the block
	/// that was read.
	///
	/// This is what playback wants: a tick is the unit a demo is replayed in, and
	/// reading one record at a time would mean stopping the loop on the first
	/// record of the *next* tick and remembering it.
	/// </summary>
	public bool TryReadTick(System.Collections.Generic.List<DemoRecord> into, out uint tick)
	{
		tick = _tick;
		into.Clear();

		// A record left over from the last call: reading a block means reading one
		// record past its end, and that one belongs to the block after this. Taken
		// before the end-of-stream check, so the last block is never dropped.
		if (_pending is { } carried)
		{
			_pending = null;
			into.Add(carried);
			tick = carried.Tick;
		}
		else if (Ended)
		{
			return false;
		}

		while (TryReadRecord(out DemoRecord record))
		{
			if (into.Count == 0)
			{
				tick = record.Tick;
			}
			else if (record.Tick != tick)
			{
				_pending = record;
				return true;
			}

			into.Add(record);
		}

		return into.Count > 0;
	}

	public void Dispose()
	{
		if (!_leaveOpen)
		{
			_stream.Dispose();
		}
	}

	private bool Truncated(string what) => Fail($"the file ends in the middle of {what}");

	private bool Fail(string reason)
	{
		Error ??= reason;
		Ended = true;
		return false;
	}

	private bool TryReadByte(out byte value)
	{
		int read = _stream.ReadByte();
		value = (byte)read;
		return read >= 0;
	}

	/// <summary>Fills the front of the scratch buffer, or returns false at the end of the stream.</summary>
	private bool TryRead(int count) => ReadUpTo(_stream, _scratch, count) == count;

	/// <summary>
	/// A stream's read may return fewer bytes than asked for without being at its
	/// end — that is the contract, and a file behind a pipe or a decompressor will
	/// take it up on it.
	/// </summary>
	private static int ReadUpTo(Stream stream, byte[] into, int count)
	{
		int read = 0;
		while (read < count)
		{
			int got;
			try
			{
				got = stream.Read(into, read, count - read);
			}
			catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException)
			{
				return read;
			}

			if (got <= 0)
			{
				return read;
			}
			read += got;
		}

		return read;
	}

	/// <summary>
	/// The bytes already read past the header, then the rest of the file. Exists so
	/// that <see cref="TryOpen"/> can over-read a variable-length header without
	/// requiring the stream to seek — a demo may well be arriving through something
	/// that cannot.
	/// </summary>
	private sealed class PrefixStream : Stream
	{
		private readonly byte[] _prefix;
		private readonly Stream _rest;
		private readonly bool _leaveOpen;
		private int _offset;

		public PrefixStream(byte[] prefix, Stream rest, bool leaveOpen)
		{
			_prefix = prefix;
			_rest = rest;
			_leaveOpen = leaveOpen;
		}

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			if (_offset < _prefix.Length)
			{
				int taken = Math.Min(count, _prefix.Length - _offset);
				Array.Copy(_prefix, _offset, buffer, offset, taken);
				_offset += taken;
				return taken;
			}

			return _rest.Read(buffer, offset, count);
		}

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing && !_leaveOpen)
			{
				_rest.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
