using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// Suppressive fire (docs/NETCODE.md §10.8): a unit with a window keeps firing at
/// the place it last saw a live target for that long, and a unit without one stops
/// when it stops seeing, as every unit always has.
/// </summary>
public class SuppressionTests
{
	/// <summary>Four seconds, the heavy gunner's.</summary>
	private const int Window = SimConfig.TickRate * 4;

	private const int Target = 7;

	private static readonly Vector3 Eye = new(0f, 1.5f, 0f);
	private static readonly Vector3 SeenAt = new(0f, 1.2f, 30f);

	private static SuppressionMemory SawAt(uint tick, int window = Window)
	{
		var memory = default(SuppressionMemory);
		Suppression.Remember(ref memory, Target, SeenAt, tick, window);
		return memory;
	}

	[Fact]
	public void AfterLosingSight_ItKeepsFiringWhereItLastSawThem_ForTheWindow()
	{
		SuppressionMemory memory = SawAt(1000);

		Assert.True(Suppression.TryPoint(memory, 1001, targetAlive: true, Eye, 45f, out Vector3 point));
		Assert.Equal(SeenAt, point);
		Assert.True(Suppression.TryPoint(memory, 1000 + Window, targetAlive: true, Eye, 45f, out _));
		Assert.False(Suppression.TryPoint(memory, 1000 + Window + 1, targetAlive: true, Eye, 45f, out _));
	}

	[Fact]
	public void AUnitWithNoWindow_RemembersNothing()
	{
		SuppressionMemory memory = SawAt(1000, window: 0);

		Assert.False(memory.IsSet);
		Assert.False(Suppression.TryPoint(memory, 1001, targetAlive: true, Eye, 45f, out _));
	}

	[Fact]
	public void SeeingThemAgain_RestartsTheWindowAtTheNewPoint()
	{
		SuppressionMemory memory = SawAt(1000);
		var moved = new Vector3(5f, 1.2f, 30f);
		Suppression.Remember(ref memory, Target, moved, 1100, Window);

		Assert.True(Suppression.TryPoint(memory, 1100 + Window, targetAlive: true, Eye, 45f, out Vector3 point));
		Assert.Equal(moved, point);
	}

	[Fact]
	public void ADeadTarget_IsNotSuppressed()
	{
		SuppressionMemory memory = SawAt(1000);

		Assert.False(Suppression.TryPoint(memory, 1001, targetAlive: false, Eye, 45f, out _));
	}

	[Fact]
	public void APointOutOfRange_IsNotFiredAt()
	{
		SuppressionMemory memory = SawAt(1000);

		Assert.False(Suppression.TryPoint(memory, 1001, targetAlive: true, Eye, 20f, out _));
		Assert.True(Suppression.TryPoint(memory, 1001, targetAlive: true, Eye, 31f, out _));
	}

	[Fact]
	public void Clearing_ForgetsIt()
	{
		SuppressionMemory memory = SawAt(1000);
		Suppression.Clear(ref memory);

		Assert.False(memory.IsSet);
		Assert.False(Suppression.TryPoint(memory, 1001, targetAlive: true, Eye, 45f, out _));
	}

	[Fact]
	public void NobodyToRemember_RemembersNothing()
	{
		var memory = default(SuppressionMemory);
		Suppression.Remember(ref memory, OwnerId.None, SeenAt, 1000, Window);

		Assert.False(memory.IsSet);
	}

	[Fact]
	public void AWindowFromTickZero_IsStillAWindow()
	{
		// UntilTick 0 means "none", so a window opened on tick 0 must not read as one.
		SuppressionMemory memory = SawAt(0);

		Assert.True(memory.IsSet);
		Assert.True(Suppression.TryPoint(memory, 1, targetAlive: true, Eye, 45f, out _));
	}

	[Fact]
	public void SuppressingCountsAsATargetToTheUnitFsm()
	{
		// What UnitManager does with the point: hands it to the FSM as a target at its
		// distance, which is what makes the unit engage, and so pull the trigger.
		var traits = new UnitTraits(45f, 45f, 1.5f, 15f);
		float distance = Eye.DistanceTo(SeenAt);
		var situation = new UnitSituation(alive: true, OrderKind.Defend, hasTarget: true, distance,
			destinationDistance: 0f);

		Assert.Equal(UnitStateId.Engaging, UnitBrain.Next(situation, traits));
	}
}
