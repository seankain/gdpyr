using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using FluidHTN;
using Godot;

namespace Gdpyr.Sim.AiDebug;

/// <summary>
/// The spectator debugger's two messages (docs/AI_DEBUG.md §5): the watch request a
/// spectator sends when what it wants changes, and the frame the server answers
/// with ten times a second.
///
/// Tasks and conditions travel as indices into the domain map both ends build
/// (<see cref="HtnDomainMap"/>), positions as the snapshot's centimetres
/// (<see cref="Quantize.MetersToI16"/>), and every count is bounded. Decoding is
/// total, as every codec here is: anything but exactly one well-formed message
/// returns false, never throws — the server reads watch requests from any peer
/// that has said it is spectating.
/// </summary>
public static class AiDebugCodec
{
	/// <summary>First byte of both messages. A spectator on another version is told nothing it would misread.</summary>
	public const byte Version = 1;

	public const int MaxLabels = SimConfig.MaxUnits + SnapshotCodec.MaxPlayers;
	public const int MaxCommanders = 4;
	public const int MaxSquads = 32;
	public const int MaxMembers = SimConfig.MaxUnits;
	public const int MaxInspected = 8;
	public const int MaxMarkers = 32;
	public const int MaxValues = 32;

	private const byte NoIndex = byte.MaxValue;

	// ---- watch -------------------------------------------------------------

	public static byte[] EncodeWatch(AiWatch watch)
	{
		var w = new Writer(16);
		w.U8(Version);
		w.U8((byte)(watch?.Flags ?? AiWatchFlags.None));
		w.I32(watch?.CommanderPeerId ?? 0);

		int count = Math.Min(watch?.Selected.Count ?? 0, AiWatch.MaxSelected);
		w.U8((byte)count);
		for (int i = 0; i < count; i++)
		{
			w.Ref(watch.Selected[i]);
		}

		return w.ToArray();
	}

	public static bool TryDecodeWatch(ReadOnlySpan<byte> payload, AiWatch into)
	{
		if (into == null)
		{
			return false;
		}

		var r = new Reader(payload);
		if (r.U8() != Version)
		{
			return false;
		}

		var flags = (AiWatchFlags)r.U8();
		int commander = r.I32();
		int count = r.U8();
		if (!r.Ok || count > AiWatch.MaxSelected
			|| (flags & ~(AiWatchFlags.Labels | AiWatchFlags.Commander)) != 0)
		{
			return false;
		}

		into.Flags = flags;
		into.CommanderPeerId = commander;
		into.Selected.Clear();
		for (int i = 0; i < count; i++)
		{
			AiEntityRef target = r.Ref();
			if (target.IsValid)
			{
				into.Selected.Add(target);
			}
		}

		return r.Ok && r.AtEnd;
	}

	// ---- frame -------------------------------------------------------------

	public static byte[] EncodeFrame(AiDebugFrame frame)
	{
		if (frame == null)
		{
			throw new ArgumentNullException(nameof(frame));
		}

		var w = new Writer(512);
		w.U8(Version);
		w.U32(frame.Tick);
		w.U8((byte)frame.BotAi);
		w.U32(frame.GroundSignature);
		w.U32(frame.UnitSignature);
		w.U32(frame.SquadSignature);

		int labels = Math.Min(frame.Labels.Count, MaxLabels);
		w.U16((ushort)labels);
		for (int i = 0; i < labels; i++)
		{
			AiLabel label = frame.Labels[i];
			w.Ref(label.Ref);
			w.U8(label.Goal);
			w.U8(label.Detail);
			w.U8((byte)label.Flags);
			if ((label.Flags & AiLabelFlags.HasPoint) != 0)
			{
				w.Point(label.Point);
			}

			w.I32(label.TargetOwnerId);
		}

		int commanders = Math.Min(frame.Commanders.Count, MaxCommanders);
		w.U8((byte)commanders);
		for (int i = 0; i < commanders; i++)
		{
			AiCommander commander = frame.Commanders[i];
			w.I32(commander.PeerId);
			w.I8(commander.BuyTimeZone);
			w.U8((byte)(commander.ReconWanted ? 1 : 0));

			int squads = Math.Min(commander.Squads.Count, MaxSquads);
			w.U8((byte)squads);
			for (int s = 0; s < squads; s++)
			{
				WriteSquad(w, commander.Squads[s]);
			}
		}

		int inspected = Math.Min(frame.Inspected.Count, MaxInspected);
		w.U8((byte)inspected);
		for (int i = 0; i < inspected; i++)
		{
			AiInspect inspect = frame.Inspected[i];
			w.Ref(inspect.Ref);
			w.U8(inspect.Goal);
			w.U8((byte)(inspect.Planning ? 1 : 0));
			WriteTrace(w, inspect.Trace);

			int markers = Math.Min(inspect.Markers.Count, MaxMarkers);
			w.U8((byte)markers);
			for (int m = 0; m < markers; m++)
			{
				w.U8((byte)inspect.Markers[m].Key);
				w.Point(inspect.Markers[m].Point);
			}

			int values = Math.Min(inspect.Values.Count, MaxValues);
			w.U8((byte)values);
			for (int v = 0; v < values; v++)
			{
				w.U8((byte)inspect.Values[v].Key);
				w.I32(inspect.Values[v].Value);
			}
		}

		return w.ToArray();
	}

	public static bool TryDecodeFrame(ReadOnlySpan<byte> payload, out AiDebugFrame frame)
	{
		frame = null;
		var r = new Reader(payload);
		if (r.U8() != Version)
		{
			return false;
		}

		var decoded = new AiDebugFrame
		{
			Tick = r.U32(),
			BotAi = (BotAi)r.U8(),
			GroundSignature = r.U32(),
			UnitSignature = r.U32(),
			SquadSignature = r.U32(),
		};

		int labels = r.U16();
		if (!r.Ok || labels > MaxLabels)
		{
			return false;
		}

		for (int i = 0; i < labels && r.Ok; i++)
		{
			var label = new AiLabel
			{
				Ref = r.Ref(),
				Goal = r.U8(),
				Detail = r.U8(),
				Flags = (AiLabelFlags)r.U8(),
			};

			if ((label.Flags & AiLabelFlags.HasPoint) != 0)
			{
				label.Point = r.Point();
			}

			label.TargetOwnerId = r.I32();
			decoded.Labels.Add(label);
		}

		int commanders = r.U8();
		if (!r.Ok || commanders > MaxCommanders)
		{
			return false;
		}

		for (int i = 0; i < commanders && r.Ok; i++)
		{
			var commander = new AiCommander
			{
				PeerId = r.I32(),
				BuyTimeZone = r.I8(),
				ReconWanted = r.U8() != 0,
			};

			int squads = r.U8();
			if (!r.Ok || squads > MaxSquads)
			{
				return false;
			}

			for (int s = 0; s < squads && r.Ok; s++)
			{
				var squad = new AiSquad();
				if (!ReadSquad(ref r, squad))
				{
					return false;
				}

				commander.Squads.Add(squad);
			}

			decoded.Commanders.Add(commander);
		}

		int inspected = r.U8();
		if (!r.Ok || inspected > MaxInspected)
		{
			return false;
		}

		for (int i = 0; i < inspected && r.Ok; i++)
		{
			var inspect = new AiInspect
			{
				Ref = r.Ref(),
				Goal = r.U8(),
				Planning = r.U8() != 0,
			};

			if (!ReadTrace(ref r, inspect.Trace))
			{
				return false;
			}

			int markers = r.U8();
			if (!r.Ok || markers > MaxMarkers)
			{
				return false;
			}

			for (int m = 0; m < markers && r.Ok; m++)
			{
				inspect.Markers.Add(new AiMarker { Key = (AiMarkerKey)r.U8(), Point = r.Point() });
			}

			int values = r.U8();
			if (!r.Ok || values > MaxValues)
			{
				return false;
			}

			for (int v = 0; v < values && r.Ok; v++)
			{
				inspect.Values.Add(new AiValue { Key = (AiValueKey)r.U8(), Value = r.I32() });
			}

			decoded.Inspected.Add(inspect);
		}

		if (!r.Ok || !r.AtEnd)
		{
			return false;
		}

		frame = decoded;
		return true;
	}

	// ---- parts -------------------------------------------------------------

	private static void WriteSquad(Writer w, AiSquad squad)
	{
		w.U8(squad.Slot);
		w.U8(squad.Role);
		w.U8(squad.Task);
		w.U8(squad.Target);
		w.U8(squad.Goal);
		w.U8(squad.Order);
		w.U8(squad.Phase);
		w.U8((byte)squad.Flags);
		w.I8(squad.Zone);
		w.I32(squad.TargetOwnerId);
		w.Strength(squad.Strength);
		w.Strength(squad.FullStrength);
		w.Strength(squad.StrengthAtFormation);
		w.Point(squad.Centroid);
		w.Point(squad.TaskPoint);
		w.Point(squad.StagingPoint);
		w.Point(squad.FallbackPoint);
		w.Point(squad.OrderPoint);

		int members = Math.Min(squad.Members.Count, MaxMembers);
		w.U8((byte)members);
		for (int i = 0; i < members; i++)
		{
			w.U16(squad.Members[i]);
		}

		WriteTrace(w, squad.Trace);
	}

	private static bool ReadSquad(ref Reader r, AiSquad squad)
	{
		squad.Slot = r.U8();
		squad.Role = r.U8();
		squad.Task = r.U8();
		squad.Target = r.U8();
		squad.Goal = r.U8();
		squad.Order = r.U8();
		squad.Phase = r.U8();
		squad.Flags = (AiSquadFlags)r.U8();
		squad.Zone = r.I8();
		squad.TargetOwnerId = r.I32();
		squad.Strength = r.Strength();
		squad.FullStrength = r.Strength();
		squad.StrengthAtFormation = r.Strength();
		squad.Centroid = r.Point();
		squad.TaskPoint = r.Point();
		squad.StagingPoint = r.Point();
		squad.FallbackPoint = r.Point();
		squad.OrderPoint = r.Point();

		int members = r.U8();
		if (!r.Ok || members > MaxMembers)
		{
			return false;
		}

		for (int i = 0; i < members && r.Ok; i++)
		{
			squad.Members.Add(r.U16());
		}

		return ReadTrace(ref r, squad.Trace);
	}

	private static void WriteTrace(Writer w, HtnTrace trace)
	{
		w.U8((byte)trace.Domain);
		if (trace.Domain == HtnDomainKind.None)
		{
			return;
		}

		w.U8(Index(trace.Current));
		w.U8((byte)trace.LastStatus);
		WriteIndices(w, trace.Planned, HtnTrace.MaxTasks);
		WriteIndices(w, trace.Paused, HtnTrace.MaxTasks);
		WriteIndices(w, trace.Traversal, HtnTrace.MaxTraversal);

		int facts = Math.Min(trace.Facts.Count, HtnTrace.MaxFacts);
		w.U8((byte)facts);
		for (int i = 0; i < facts; i++)
		{
			w.U8(trace.Facts[i]);
		}

		int holds = Math.Min(trace.Holds.Count, HtnDomainMap.MaxConditions);
		w.U8((byte)holds);
		for (int i = 0; i < holds; i += 8)
		{
			byte bits = 0;
			for (int b = 0; b < 8 && i + b < holds; b++)
			{
				bits |= (byte)(trace.Holds[i + b] ? 1 << b : 0);
			}

			w.U8(bits);
		}

		int events = Math.Min(trace.History.Count, HtnHistory.Capacity);
		w.U8((byte)events);
		for (int i = trace.History.Count - events; i < trace.History.Count; i++)
		{
			HtnEvent e = trace.History[i];
			w.U32(e.Tick);
			w.U8((byte)e.Kind);
			w.U8(e.Node);
			w.U8(e.Other);
		}
	}

	private static bool ReadTrace(ref Reader r, HtnTrace trace)
	{
		trace.Clear();
		var domain = (HtnDomainKind)r.U8();
		if (!r.Ok || domain > HtnDomainKind.Squad)
		{
			return false;
		}

		trace.Domain = domain;
		if (domain == HtnDomainKind.None)
		{
			return true;
		}

		byte current = r.U8();
		trace.Current = current == NoIndex ? -1 : current;
		trace.LastStatus = (TaskStatus)r.U8();

		if (!ReadIndices(ref r, trace.Planned, HtnTrace.MaxTasks)
			|| !ReadIndices(ref r, trace.Paused, HtnTrace.MaxTasks)
			|| !ReadIndices(ref r, trace.Traversal, HtnTrace.MaxTraversal))
		{
			return false;
		}

		int facts = r.U8();
		if (!r.Ok || facts > HtnTrace.MaxFacts)
		{
			return false;
		}

		for (int i = 0; i < facts && r.Ok; i++)
		{
			trace.Facts.Add(r.U8());
		}

		int holds = r.U8();
		if (!r.Ok)
		{
			return false;
		}

		for (int i = 0; i < holds && r.Ok; i += 8)
		{
			byte bits = r.U8();
			for (int b = 0; b < 8 && i + b < holds; b++)
			{
				trace.Holds.Add((bits & (1 << b)) != 0);
			}
		}

		int events = r.U8();
		if (!r.Ok || events > HtnHistory.Capacity)
		{
			return false;
		}

		for (int i = 0; i < events && r.Ok; i++)
		{
			trace.History.Add(new HtnEvent
			{
				Tick = r.U32(),
				Kind = (HtnEventKind)r.U8(),
				Node = r.U8(),
				Other = r.U8(),
			});
		}

		return r.Ok;
	}

	private static void WriteIndices(Writer w, List<int> indices, int max)
	{
		int count = Math.Min(indices.Count, max);
		w.U8((byte)count);
		for (int i = 0; i < count; i++)
		{
			w.U8(Index(indices[i]));
		}
	}

	private static bool ReadIndices(ref Reader r, List<int> into, int max)
	{
		int count = r.U8();
		if (!r.Ok || count > max)
		{
			return false;
		}

		for (int i = 0; i < count && r.Ok; i++)
		{
			byte index = r.U8();
			into.Add(index == NoIndex ? -1 : index);
		}

		return r.Ok;
	}

	private static byte Index(int index) => index >= 0 && index < NoIndex ? (byte)index : NoIndex;

	// ---- bytes -------------------------------------------------------------

	private sealed class Writer
	{
		private byte[] _buffer;
		private int _length;

		public Writer(int capacity) => _buffer = new byte[Math.Max(capacity, 16)];

		public void U8(byte value) => Take(1)[0] = value;

		public void I8(int value) => U8((byte)(sbyte)Math.Clamp(value, sbyte.MinValue, sbyte.MaxValue));

		public void U16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), value);

		public void I16(short value) => BinaryPrimitives.WriteInt16LittleEndian(Take(2), value);

		public void I32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), value);

		public void U32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), value);

		public void Point(Vector3 point)
		{
			I16(Quantize.MetersToI16(point.X));
			I16(Quantize.MetersToI16(point.Y));
			I16(Quantize.MetersToI16(point.Z));
		}

		/// <summary>A strength — cost times health — in whole points, to 65,535.</summary>
		public void Strength(float value) => U16((ushort)Math.Clamp(MathF.Round(value), 0f, ushort.MaxValue));

		public void Ref(AiEntityRef target)
		{
			U8((byte)target.Kind);
			I32(target.Id);
			U8(target.Slot);
		}

		public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

		private Span<byte> Take(int count)
		{
			if (_length + count > _buffer.Length)
			{
				Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
			}

			Span<byte> span = _buffer.AsSpan(_length, count);
			_length += count;
			return span;
		}
	}

	/// <summary>Reads forward; a read past the end returns 0 and clears <see cref="Ok"/> for good.</summary>
	private ref struct Reader
	{
		private readonly ReadOnlySpan<byte> _bytes;
		private int _at;

		public Reader(ReadOnlySpan<byte> bytes)
		{
			_bytes = bytes;
			_at = 0;
			Ok = true;
		}

		public bool Ok { get; private set; }

		public bool AtEnd => _at == _bytes.Length;

		public byte U8() => Has(1) ? _bytes[_at++] : (byte)0;

		public int I8() => (sbyte)U8();

		public ushort U16() => Has(2) ? BinaryPrimitives.ReadUInt16LittleEndian(Advance(2)) : (ushort)0;

		public short I16() => Has(2) ? BinaryPrimitives.ReadInt16LittleEndian(Advance(2)) : (short)0;

		public int I32() => Has(4) ? BinaryPrimitives.ReadInt32LittleEndian(Advance(4)) : 0;

		public uint U32() => Has(4) ? BinaryPrimitives.ReadUInt32LittleEndian(Advance(4)) : 0u;

		public Vector3 Point() =>
			new(Quantize.I16ToMeters(I16()), Quantize.I16ToMeters(I16()), Quantize.I16ToMeters(I16()));

		public float Strength() => U16();

		public AiEntityRef Ref()
		{
			var kind = (AiEntityKind)U8();
			int id = I32();
			byte slot = U8();
			return kind is AiEntityKind.Player or AiEntityKind.Unit or AiEntityKind.Squad
				? new AiEntityRef(kind, id, slot)
				: default;
		}

		private bool Has(int count)
		{
			if (Ok && _at + count <= _bytes.Length)
			{
				return true;
			}

			Ok = false;
			return false;
		}

		private ReadOnlySpan<byte> Advance(int count)
		{
			ReadOnlySpan<byte> span = _bytes.Slice(_at, count);
			_at += count;
			return span;
		}
	}
}
