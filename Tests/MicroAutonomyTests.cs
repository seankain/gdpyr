using System;
using Gdpyr.Sim;
using Gdpyr.Sim.AiDebug;
using Gdpyr.Sim.Htn;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Micro-autonomy (docs/TACTICS.md §5.9): the strategist-only state message and its
/// total decoder, and the fact the unit's planner observes it by.
/// </summary>
public class MicroAutonomyTests
{
	private static readonly UnitTraits Rifleman = new(45f, 40f, 1.5f, 20f, UnitDoctrine.Rifleman);

	// ---- ServerAutonomyState ------------------------------------------------

	[Fact]
	public void TheState_RoundTrips_DefaultAndExceptions()
	{
		UnitAutonomyRecord[] records =
		{
			new() { UnitId = 3, Mode = MicroAutonomy.Off },
			new() { UnitId = 65535, Mode = MicroAutonomy.Off },
			new() { UnitId = 1, Mode = MicroAutonomy.On },
		};

		byte[] payload = AutonomyStateCodec.Encode(MicroAutonomy.On, records);
		Assert.Equal(AutonomyStateCodec.PayloadBytes(3), payload.Length);

		var decoded = new UnitAutonomyRecord[AutonomyStateCodec.MaxRecords];
		Assert.True(AutonomyStateCodec.TryDecode(payload, decoded, out MicroAutonomy teamDefault, out int count));
		Assert.Equal(MicroAutonomy.On, teamDefault);
		Assert.Equal(3, count);
		for (int i = 0; i < count; i++)
		{
			Assert.Equal(records[i].UnitId, decoded[i].UnitId);
			Assert.Equal(records[i].Mode, decoded[i].Mode);
		}
	}

	[Fact]
	public void TheState_WithNoExceptions_IsThreeBytes()
	{
		byte[] payload = AutonomyStateCodec.Encode(MicroAutonomy.Off, ReadOnlySpan<UnitAutonomyRecord>.Empty);
		Assert.Equal(3, payload.Length);

		var decoded = new UnitAutonomyRecord[1];
		Assert.True(AutonomyStateCodec.TryDecode(payload, decoded, out MicroAutonomy teamDefault, out int count));
		Assert.Equal(MicroAutonomy.Off, teamDefault);
		Assert.Equal(0, count);
	}

	[Fact]
	public void AFullField_FitsAndRoundTrips()
	{
		var records = new UnitAutonomyRecord[AutonomyStateCodec.MaxRecords + 5];
		for (int i = 0; i < records.Length; i++)
		{
			records[i] = new UnitAutonomyRecord { UnitId = (ushort)(i + 1), Mode = MicroAutonomy.On };
		}

		// Never more exceptions than units on the field: the rest are left off.
		byte[] payload = AutonomyStateCodec.Encode(MicroAutonomy.Off, records);
		Assert.Equal(AutonomyStateCodec.MaxPayloadBytes, payload.Length);

		var decoded = new UnitAutonomyRecord[AutonomyStateCodec.MaxRecords];
		Assert.True(AutonomyStateCodec.TryDecode(payload, decoded, out _, out int count));
		Assert.Equal(AutonomyStateCodec.MaxRecords, count);
	}

	[Fact]
	public void EncodingIntoTooSmallABuffer_WritesNothing()
	{
		UnitAutonomyRecord[] records = { new() { UnitId = 3, Mode = MicroAutonomy.On } };
		Assert.Equal(0, AutonomyStateCodec.Encode(MicroAutonomy.Off, records, new byte[5]));
	}

	[Fact]
	public void AMalformedState_IsRefused_NeverThrown()
	{
		var into = new UnitAutonomyRecord[AutonomyStateCodec.MaxRecords];
		byte[] good = AutonomyStateCodec.Encode(MicroAutonomy.On,
			new UnitAutonomyRecord[] { new() { UnitId = 9, Mode = MicroAutonomy.Off } });

		// Short.
		Assert.False(AutonomyStateCodec.TryDecode(good.AsSpan(0, 2), into, out _, out _));
		Assert.False(AutonomyStateCodec.TryDecode(good.AsSpan(0, good.Length - 1), into, out _, out _));
		Assert.False(AutonomyStateCodec.TryDecode(null, into, out _, out _));

		// A default or a mode that names no mode.
		byte[] badDefault = (byte[])good.Clone();
		badDefault[0] = 2;
		Assert.False(AutonomyStateCodec.TryDecode(badDefault, into, out _, out _));
		byte[] badMode = (byte[])good.Clone();
		badMode[AutonomyStateCodec.HeaderBytes + 2] = 7;
		Assert.False(AutonomyStateCodec.TryDecode(badMode, into, out _, out _));

		// More records than a field holds, or than the caller has room for.
		byte[] tooMany = new byte[AutonomyStateCodec.PayloadBytes(AutonomyStateCodec.MaxRecords + 1)];
		tooMany[1] = (byte)((AutonomyStateCodec.MaxRecords + 1) & 0xFF);
		tooMany[2] = (byte)((AutonomyStateCodec.MaxRecords + 1) >> 8);
		Assert.False(AutonomyStateCodec.TryDecode(tooMany, into, out _, out _));
		Assert.False(AutonomyStateCodec.TryDecode(good, Span<UnitAutonomyRecord>.Empty, out _, out _));

		// Every truncation and every byte value of a valid payload, without a throw.
		for (int length = 0; length <= good.Length; length++)
		{
			AutonomyStateCodec.TryDecode(good.AsSpan(0, length), into, out _, out _);
		}

		for (int value = 0; value < 256; value++)
		{
			for (int at = 0; at < good.Length; at++)
			{
				byte[] mutated = (byte[])good.Clone();
				mutated[at] = (byte)value;
				AutonomyStateCodec.TryDecode(mutated, into, out _, out _);
			}
		}
	}

	// ---- the planner's fact ---------------------------------------------------

	[Fact]
	public void TheUnitsPlanner_ObservesItsMicroAutonomy_AsAFact()
	{
		var planning = new UnitPlanning(UnitPlanTraits.Default);
		UnitContext plan = planning.CreateContext(Rifleman);

		Assert.Equal((byte)MicroAutonomy.Off, plan.Get(UnitFact.MicroAutonomy));

		plan.Tick = 1;
		plan.Track(Vector3.Zero, UnitOrder.Hold(Vector3.Zero), hasTarget: false, Vector3.Zero);
		planning.Tick(plan);
		Assert.False(plan.IsDirty);

		// The same value again is no change; a switch is a replan, as any changed fact is.
		plan.SenseAutonomy(MicroAutonomy.Off);
		Assert.False(plan.IsDirty);
		plan.SenseAutonomy(MicroAutonomy.On);
		Assert.Equal((byte)MicroAutonomy.On, plan.Get(UnitFact.MicroAutonomy));
		Assert.True(plan.IsDirty);
	}

	[Fact]
	public void SwitchingIt_ReplansOnTheTick_AndLeavesTodaysPlanAsItWas()
	{
		// T0 adds the fact and no method reads it yet: on or off, a unit plans the same.
		var planning = new UnitPlanning(UnitPlanTraits.Default);
		UnitContext on = planning.CreateContext(Rifleman);
		UnitContext off = planning.CreateContext(Rifleman);
		var order = new UnitOrder { Kind = OrderKind.Move, Target = new Vector3(30f, 0f, 0f), Anchor = Vector3.Zero };

		for (uint tick = 1; tick <= 120; tick++)
		{
			on.Tick = tick;
			off.Tick = tick;
			on.SenseAutonomy(tick >= 60 ? MicroAutonomy.On : MicroAutonomy.Off);
			off.SenseAutonomy(MicroAutonomy.Off);
			on.Track(Vector3.Zero, order, hasTarget: false, Vector3.Zero);
			off.Track(Vector3.Zero, order, hasTarget: false, Vector3.Zero);
			planning.Tick(on);
			planning.Tick(off);

			Assert.Equal(off.Intent.Goal, on.Intent.Goal);
			Assert.Equal(off.Intent.Move, on.Intent.Move);
		}

		Assert.Equal(UnitGoal.Obey, on.Intent.Goal);
	}

	[Fact]
	public void TheDebugger_NamesTheFactAndItsValues()
	{
		int fact = (int)UnitFact.MicroAutonomy;
		Assert.Equal("MicroAutonomy", HtnSchema.Unit.FactName(fact));
		Assert.Equal("On", HtnSchema.Unit.FactValue(fact, (byte)MicroAutonomy.On));
		Assert.Equal("Off", HtnSchema.Unit.FactValue(fact, (byte)MicroAutonomy.Off));
		Assert.True((int)UnitFact.Count <= HtnTrace.MaxFacts);
	}

	[Fact]
	public void UnitTraits_CarryTheDoctrine_AndANewOneHasNone()
	{
		Assert.Equal(UnitDoctrine.Rifleman, Rifleman.Doctrine);
		Assert.Equal(UnitDoctrine.None, new UnitTraits(45f, 40f, 1.5f, 20f).Doctrine);
	}
}
