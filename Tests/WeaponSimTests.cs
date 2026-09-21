using Gdpyr.Sim;
using Xunit;

namespace Gdpyr.Tests;

public class WeaponSimTests
{
	private static WeaponStats Pistol() =>
		new(FireMode.Semi, WeaponStats.TicksPerShot(400f), magazineSize: 12, reloadTicks: 90, damage: 25f);

	private static WeaponStats Rifle() =>
		new(FireMode.Auto, WeaponStats.TicksPerShot(600f), magazineSize: 30, reloadTicks: 120, damage: 20f);

	private static WeaponStats Launcher() =>
		new(FireMode.Single, WeaponStats.TicksPerShot(60f), magazineSize: 1, reloadTicks: 150, damage: 90f);

	private static WeaponStats Hammer() =>
		new(FireMode.Semi, WeaponStats.TicksPerShot(60f), magazineSize: 0, reloadTicks: 0, damage: 75f,
			isMelee: true, rangeMeters: 2.5f, sweepRadiusMeters: 0.35f);

	private static InputContext Input(InputButtons held, InputButtons previous = InputButtons.None) =>
		new(InputFrame.Create(0, 0f, 0f, 0f, 0f, held), (ushort)previous, SimConfig.TickDelta);

	/// <summary>Holds the trigger from <paramref name="fromTick"/> and counts the shots.</summary>
	private static int FireFor(ref WeaponState state, in WeaponStats stats, uint fromTick, int ticks,
		bool holdTrigger = true)
	{
		int shots = 0;
		InputButtons previous = InputButtons.None;
		for (int i = 0; i < ticks; i++)
		{
			InputButtons held = holdTrigger || i == 0 ? InputButtons.Fire : InputButtons.None;
			if (WeaponSim.Step(ref state, stats, Input(held, previous), fromTick + (uint)i) == WeaponAction.Fire)
			{
				shots++;
			}
			previous = held;
		}
		return shots;
	}

	[Fact]
	public void RpmBecomesAWholeNumberOfTicks()
	{
		// 600 RPM is 10 rounds a second, which is a shot every 6 ticks at 60 Hz.
		Assert.Equal(6, WeaponStats.TicksPerShot(600f));
		Assert.Equal(9, WeaponStats.TicksPerShot(400f));

		// Never zero, whatever nonsense a resource carries: a weapon that fires on
		// every tick would empty a magazine in a fifth of a second, but one that
		// fires infinitely often inside a tick is a divide by zero.
		Assert.Equal(1, WeaponStats.TicksPerShot(0f));
		Assert.Equal(1, WeaponStats.TicksPerShot(-100f));
		Assert.Equal(1, WeaponStats.TicksPerShot(100_000f));
	}

	[Fact]
	public void AutoFiresAtItsRateWhileHeld()
	{
		WeaponStats stats = Rifle();
		WeaponState state = WeaponState.Ready(3, stats);

		// One second of held trigger at 600 RPM: the first tick fires, then every
		// sixth after it.
		int shots = FireFor(ref state, stats, 0, 60);

		Assert.Equal(10, shots);
		Assert.Equal(20, state.Ammo);
	}

	[Fact]
	public void SemiFiresOncePerPull()
	{
		WeaponStats stats = Pistol();
		WeaponState state = WeaponState.Ready(1, stats);

		int shots = FireFor(ref state, stats, 0, 60);

		Assert.Equal(1, shots);
		Assert.Equal(11, state.Ammo);
	}

	[Fact]
	public void SemiCannotOutrunItsRateOfFire()
	{
		WeaponStats stats = Pistol();
		WeaponState state = WeaponState.Ready(1, stats);
		int shots = 0;

		// Trigger pulled on every other tick — faster than the weapon cycles.
		for (uint tick = 0; tick < 60; tick++)
		{
			InputButtons held = tick % 2 == 0 ? InputButtons.Fire : InputButtons.None;
			InputButtons previous = tick % 2 == 0 ? InputButtons.None : InputButtons.Fire;
			if (WeaponSim.Step(ref state, stats, Input(held, previous), tick) == WeaponAction.Fire)
			{
				shots++;
			}
		}

		// 9 ticks per shot, and the trigger is only up on even ticks, so the shots
		// land on 0, 10, 20, 30, 40 and 50: six in a second, not the thirty a player
		// mashing the key asked for.
		Assert.Equal(6, shots);
	}

	[Fact]
	public void MagazineRunsDryAndReloads()
	{
		WeaponStats stats = Rifle();
		WeaponState state = WeaponState.Ready(3, stats);

		int shots = FireFor(ref state, stats, 0, 30 * 6);
		Assert.Equal(30, shots);
		Assert.Equal(0, state.Ammo);

		// The next trigger pull on an empty magazine starts a reload rather than
		// clicking, and nothing fires while it runs.
		Assert.Equal(WeaponAction.ReloadStart,
			WeaponSim.Step(ref state, stats, Input(InputButtons.Fire, InputButtons.Fire), 200));
		Assert.True(state.IsReloading);
		Assert.Equal(0, FireFor(ref state, stats, 201, stats.ReloadTicks - 1));

		Assert.Equal(WeaponAction.ReloadFinish,
			WeaponSim.Step(ref state, stats, Input(InputButtons.Fire, InputButtons.Fire), 200 + (uint)stats.ReloadTicks));
		Assert.Equal(30, state.Ammo);
	}

	[Fact]
	public void ReloadDoesNotFireOnItsLastTick()
	{
		// A held trigger that fires on the same tick the magazine seats would make a
		// reload finish half a shot early, and differently on a client that replayed
		// the tick.
		WeaponStats stats = Rifle();
		WeaponState state = WeaponState.Ready(3, stats);
		state.Ammo = 0;

		WeaponSim.Step(ref state, stats, Input(InputButtons.Fire), 100);
		uint finishTick = state.ReloadEndTick;

		Assert.Equal(WeaponAction.ReloadFinish,
			WeaponSim.Step(ref state, stats, Input(InputButtons.Fire, InputButtons.Fire), finishTick));
		Assert.Equal(WeaponAction.Fire,
			WeaponSim.Step(ref state, stats, Input(InputButtons.Fire, InputButtons.Fire), finishTick + 1));
	}

	[Fact]
	public void ManualReloadIsIgnoredOnAFullMagazine()
	{
		WeaponStats stats = Pistol();
		WeaponState state = WeaponState.Ready(1, stats);

		Assert.Equal(WeaponAction.None, WeaponSim.Step(ref state, stats, Input(InputButtons.Reload), 10));
		Assert.False(state.IsReloading);
	}

	[Fact]
	public void ManualReloadTopsUpAPartialMagazine()
	{
		WeaponStats stats = Pistol();
		WeaponState state = WeaponState.Ready(1, stats);
		state.Ammo = 4;

		Assert.Equal(WeaponAction.ReloadStart, WeaponSim.Step(ref state, stats, Input(InputButtons.Reload), 10));
		Assert.Equal(WeaponAction.ReloadFinish,
			WeaponSim.Step(ref state, stats, Input(InputButtons.None), 10 + (uint)stats.ReloadTicks));
		Assert.Equal(12, state.Ammo);
	}

	[Fact]
	public void SingleShotCyclesBetweenRounds()
	{
		WeaponStats stats = Launcher();
		WeaponState state = WeaponState.Ready(4, stats);

		Assert.Equal(WeaponAction.Fire, WeaponSim.Step(ref state, stats, Input(InputButtons.Fire), 0));
		Assert.Equal(0, state.Ammo);

		// Pulling again cycles the launcher; the shot comes after the reload.
		Assert.Equal(WeaponAction.ReloadStart,
			WeaponSim.Step(ref state, stats, Input(InputButtons.Fire), 60));
		Assert.Equal(WeaponAction.ReloadFinish,
			WeaponSim.Step(ref state, stats, Input(InputButtons.None), 60 + (uint)stats.ReloadTicks));
		Assert.Equal(1, state.Ammo);
	}

	[Fact]
	public void MeleeNeverRunsOutOfAmmunition()
	{
		WeaponStats stats = Hammer();
		WeaponState state = WeaponState.Ready(0, stats);

		int swings = FireFor(ref state, stats, 0, 600, holdTrigger: false);

		Assert.Equal(1, swings);
		Assert.Equal(0, state.Ammo);
		Assert.True(stats.HasUnlimitedAmmo);
	}

	[Fact]
	public void WeaponWithoutAReloadDryFires()
	{
		// A magazine that cannot be refilled is the one case where the trigger does
		// nothing at all, and it should say so rather than silently doing nothing.
		var stats = new WeaponStats(FireMode.Semi, 6, magazineSize: 1, reloadTicks: 0, damage: 10f);
		WeaponState state = WeaponState.Ready(7, stats);

		Assert.Equal(WeaponAction.Fire, WeaponSim.Step(ref state, stats, Input(InputButtons.Fire), 0));
		Assert.Equal(WeaponAction.DryFire,
			WeaponSim.Step(ref state, stats, Input(InputButtons.Fire), 60));
	}

	[Fact]
	public void SameInputsProduceTheSameShots()
	{
		// The client predicts a tracer by running exactly this function on exactly
		// this frame (docs/NETCODE.md §4.3); if it drifted from the server's copy the
		// prediction would be worse than useless.
		WeaponStats stats = Rifle();
		WeaponState a = WeaponState.Ready(3, stats);
		WeaponState b = WeaponState.Ready(3, stats);

		int shotsA = FireFor(ref a, stats, 500, 300);
		int shotsB = FireFor(ref b, stats, 500, 300);

		Assert.Equal(shotsA, shotsB);
		Assert.Equal(a.Ammo, b.Ammo);
		Assert.Equal(a.NextFireTick, b.NextFireTick);
		Assert.Equal(a.ReloadEndTick, b.ReloadEndTick);
	}

	[Fact]
	public void SlotSelectionIsEdgeTriggered()
	{
		Assert.Equal(1, WeaponSim.SelectSlot(0, Input(InputButtons.Weapon2)));
		Assert.Equal(2, WeaponSim.SelectSlot(0, Input(InputButtons.Weapon3)));
		Assert.Equal(0, WeaponSim.SelectSlot(0, Input(InputButtons.Weapon1)));

		// Held, not pressed: a key held across a tick boundary must not re-select,
		// or a replayed tick and the original would disagree.
		Assert.Equal(2, WeaponSim.SelectSlot(2, Input(InputButtons.Weapon2, InputButtons.Weapon2)));

		Assert.Equal(1, WeaponSim.SelectSlot(1, Input(InputButtons.None)));
	}

	[Fact]
	public void LoadoutIndexesBySlot()
	{
		var loadout = new LoadoutSelection { Melee = 0, Sidearm = 1, Large = 4 };

		Assert.Equal(0, loadout[0]);
		Assert.Equal(1, loadout[1]);
		Assert.Equal(4, loadout[2]);
	}

	[Fact]
	public void SecondsToTicksRoundsToAWholeTick()
	{
		Assert.Equal(60, WeaponStats.SecondsToTicks(1f));
		Assert.Equal(90, WeaponStats.SecondsToTicks(1.5f));
		Assert.Equal(0, WeaponStats.SecondsToTicks(0f));
		Assert.Equal(1, WeaponStats.SecondsToTicks(0.001f));
	}

	[Fact]
	public void WeaponFlagsRoundTrip()
	{
		for (int slot = 0; slot < SimConfig.WeaponSlots; slot++)
		{
			byte packed = WeaponFlags.Pack(slot, reloading: true);
			Assert.Equal(slot, WeaponFlags.Slot(packed));
			Assert.True(WeaponFlags.IsReloading(packed));

			packed = WeaponFlags.Pack(slot, reloading: false);
			Assert.Equal(slot, WeaponFlags.Slot(packed));
			Assert.False(WeaponFlags.IsReloading(packed));
		}

		// A byte off the wire carries whatever a hostile client put in it.
		Assert.InRange(WeaponFlags.Slot(0xFF), 0, SimConfig.WeaponSlots - 1);
	}
}
