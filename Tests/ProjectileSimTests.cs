using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

public class ProjectileSimTests
{
	private const byte Bullet = 1;
	private const byte Shell = 2;

	private static ProjectileStats[] Definitions()
	{
		var table = new ProjectileStats[3];
		table[Bullet] = new ProjectileStats(
			ProjectileParams.FromGramsAndMillimeters(4f, 2.8f, 0.295f),
			muzzleVelocity: 400f, damage: 25f);
		table[Shell] = new ProjectileStats(
			ProjectileParams.FromGramsAndMillimeters(230f, 20f, 0.6f),
			muzzleVelocity: 76f, damage: 60f, explosionRadiusMeters: 5f, explosionDamage: 90f,
			sweepRadiusMeters: 0.1f);
		return table;
	}

	private static ProjectileSim NewSim(int capacity = 8) => new(Definitions(), capacity);

	[Fact]
	public void SpawnPutsAProjectileInTheAirAtMuzzleVelocity()
	{
		ProjectileSim sim = NewSim();
		uint id = sim.Spawn(100, ownerPeerId: 2, Bullet, Vector3.Zero, Vector3.Forward, catchUpTicks: 0);

		Assert.NotEqual(0u, id);
		Assert.Equal(1, sim.LiveCount);
		Assert.True(sim.TryGet(id, out Projectile p));
		Assert.Equal(400f, p.Velocity.Length(), 2);
		Assert.Equal(2, p.OwnerPeerId);
		Assert.Equal(100u, p.SpawnTick);
	}

	[Fact]
	public void SpawnNormalizesTheDirection()
	{
		ProjectileSim sim = NewSim();
		uint id = sim.Spawn(0, 1, Bullet, Vector3.Zero, new Vector3(0f, 0f, -17f), 0);

		Assert.True(sim.TryGet(id, out Projectile p));
		Assert.Equal(400f, p.Velocity.Length(), 2);
	}

	[Fact]
	public void SpawnWithoutADirectionIsRefused()
	{
		ProjectileSim sim = NewSim();

		Assert.False(sim.TrySpawn(1, 0, 1, Bullet, Vector3.Zero, Vector3.Zero, 0, out _));
		Assert.Equal(0, sim.LiveCount);
	}

	[Fact]
	public void StepEmitsOneChordPerProjectile()
	{
		ProjectileSim sim = NewSim();
		sim.Spawn(0, 1, Bullet, Vector3.Zero, Vector3.Forward, 0);
		sim.Spawn(0, 2, Bullet, Vector3.Up, Vector3.Forward, 0);

		Assert.Equal(2, sim.Step(SimConfig.TickDelta));
		Assert.Equal(2, sim.Segments.Length);

		ProjectileSegment first = sim.Segments[0];
		Assert.Equal(Vector3.Zero, first.From);

		// One tick at 400 m/s is 6.67 m, less whatever drag took off it.
		Assert.InRange(first.Delta.Length(), 6.4f, 400f * SimConfig.TickDelta);
	}

	[Fact]
	public void ChordCoversTheWholeTick()
	{
		// The reason hits are tested against a segment: at 400 m/s a round covers
		// 6.7 m per tick, and anything that is not a segment test tunnels
		// (docs/NETCODE.md §4.2).
		ProjectileSim sim = NewSim();
		sim.Spawn(0, 1, Bullet, Vector3.Zero, Vector3.Forward, 0);
		sim.Step(SimConfig.TickDelta);

		Assert.True(sim.Segments[0].Delta.Length() > 6f);
	}

	[Fact]
	public void CatchUpAdvancesAProjectileFurtherOnItsFirstStep()
	{
		// docs/NETCODE.md §4.3: the server advances a shot by the shooter's one-way
		// latency so it arrives where they aimed.
		ProjectileSim plain = NewSim();
		ProjectileSim compensated = NewSim();
		plain.Spawn(0, 1, Bullet, Vector3.Zero, Vector3.Forward, catchUpTicks: 0);
		compensated.Spawn(0, 1, Bullet, Vector3.Zero, Vector3.Forward, catchUpTicks: 6);

		plain.Step(SimConfig.TickDelta);
		compensated.Step(SimConfig.TickDelta);

		Assert.True(compensated.Segments[0].Delta.Length() > plain.Segments[0].Delta.Length() * 6f);

		// And only on the first step: the debt is paid once, after which the
		// compensated round is an ordinary round that happens to be further along —
		// and therefore slower, since drag has had seven ticks at it rather than one.
		plain.Step(SimConfig.TickDelta);
		compensated.Step(SimConfig.TickDelta);
		Assert.True(compensated.Segments[0].Delta.Length() < plain.Segments[0].Delta.Length());
		Assert.True(compensated.Segments[0].Delta.Length() > plain.Segments[0].Delta.Length() * 0.9f);
	}

	[Fact]
	public void CatchUpIsCapped()
	{
		ProjectileSim sim = NewSim();
		uint id = sim.Spawn(0, 1, Bullet, Vector3.Zero, Vector3.Forward, catchUpTicks: 10_000);
		sim.Step(SimConfig.TickDelta);

		Assert.True(sim.TryGet(id, out Projectile p));
		float capped = (SimConfig.MaxProjectileCatchUpTicks + 1) * SimConfig.TickDelta;
		Assert.True(p.Age <= capped + 1e-4f, $"{p.Age}s exceeded the {capped}s cap");
	}

	[Fact]
	public void CatchUpAndPlainFlightAgreeOnWhereAProjectileIs()
	{
		// Fast-forwarding is not a shortcut with a different answer: a client that
		// catches a projectile up must land on the trajectory everyone else is on.
		ProjectileSim stepped = NewSim();
		ProjectileSim jumped = NewSim();
		uint a = stepped.Spawn(0, 1, Bullet, Vector3.Zero, Vector3.Forward, 0);
		uint b = jumped.Spawn(0, 1, Bullet, Vector3.Zero, Vector3.Forward, catchUpTicks: 5);

		for (int i = 0; i < 6; i++)
		{
			stepped.Step(SimConfig.TickDelta);
		}
		jumped.Step(SimConfig.TickDelta);

		Assert.True(stepped.TryGet(a, out Projectile first));
		Assert.True(jumped.TryGet(b, out Projectile second));
		Assert.True((first.Position - second.Position).Length() < 1e-3f, $"{first.Position} vs {second.Position}");
	}

	[Fact]
	public void ProjectilesExpireAfterTheirLifetime()
	{
		var table = Definitions();
		table[Bullet] = new ProjectileStats(table[Bullet].Physics, 400f, 25f, lifetimeSeconds: 0.1f);
		var sim = new ProjectileSim(table, 8);
		sim.Spawn(0, 1, Bullet, Vector3.Zero, Vector3.Forward, 0);

		// 0.1 s is six ticks; the round is still in the air after five and gone by
		// seven, whichever side of the boundary float accumulation lands on.
		for (int i = 0; i < 5; i++)
		{
			sim.Step(SimConfig.TickDelta);
		}
		Assert.Equal(1, sim.LiveCount);

		sim.Step(SimConfig.TickDelta);
		sim.Step(SimConfig.TickDelta);
		Assert.Equal(0, sim.LiveCount);
	}

	[Fact]
	public void TheExpiringTickStillProducesAChord()
	{
		// A round that reaches its target on the last tick of its life has hit it.
		var table = Definitions();
		table[Bullet] = new ProjectileStats(table[Bullet].Physics, 400f, 25f, lifetimeSeconds: SimConfig.TickDelta);
		var sim = new ProjectileSim(table, 8);
		sim.Spawn(0, 1, Bullet, Vector3.Zero, Vector3.Forward, 0);

		Assert.Equal(1, sim.Step(SimConfig.TickDelta));
		Assert.Equal(0, sim.LiveCount);
	}

	[Fact]
	public void ProjectilesBelowTheKillPlaneAreDropped()
	{
		ProjectileSim sim = NewSim();
		sim.Spawn(0, 1, Bullet, new Vector3(0f, SimConfig.ProjectileKillPlaneY + 1f, 0f), Vector3.Down, 0);

		sim.Step(SimConfig.TickDelta);

		Assert.Equal(0, sim.LiveCount);
	}

	[Fact]
	public void KillTakesAProjectileOutOfTheAir()
	{
		ProjectileSim sim = NewSim();
		uint id = sim.Spawn(0, 1, Bullet, Vector3.Zero, Vector3.Forward, 0);

		Assert.True(sim.Kill(id));
		Assert.Equal(0, sim.LiveCount);
		Assert.False(sim.TryGet(id, out _));

		// Killing a projectile the server already reaped is what a late hit message
		// looks like on a client, and it is not an error.
		Assert.False(sim.Kill(id));
	}

	[Fact]
	public void DeadSlotsAreReusedRatherThanExhaustingThePool()
	{
		ProjectileSim sim = NewSim(capacity: 2);

		for (int i = 0; i < 100; i++)
		{
			uint id = sim.Spawn((uint)i, 1, Bullet, Vector3.Zero, Vector3.Forward, 0);
			Assert.NotEqual(0u, id);
			sim.Step(SimConfig.TickDelta);
			sim.Kill(id);
		}

		Assert.Equal(0, sim.Overflows);
	}

	[Fact]
	public void AFullPoolRefusesRatherThanGrowing()
	{
		// No allocation in the projectile path, ever: the pool is a budget, and
		// overrunning it is a number to look at, not a resize
		// (docs/IMPLEMENTATION_PLAN.md §6).
		ProjectileSim sim = NewSim(capacity: 2);
		sim.Spawn(0, 1, Bullet, Vector3.Zero, Vector3.Forward, 0);
		sim.Spawn(0, 1, Bullet, Vector3.Zero, Vector3.Forward, 0);

		Assert.Equal(0u, sim.Spawn(0, 1, Bullet, Vector3.Zero, Vector3.Forward, 0));
		Assert.Equal(1, sim.Overflows);
		Assert.Equal(2, sim.LiveCount);
	}

	[Fact]
	public void IdsAreUnique()
	{
		ProjectileSim sim = NewSim(capacity: 64);
		var seen = new System.Collections.Generic.HashSet<uint>();

		for (int i = 0; i < 64; i++)
		{
			Assert.True(seen.Add(sim.Spawn(0, 1, Bullet, Vector3.Zero, Vector3.Forward, 0)));
		}
	}

	[Fact]
	public void ClientAndServerReproduceTheSameTrajectory()
	{
		// The client is given nothing but the spawn record (docs/NETCODE.md §4.2).
		ProjectileSim server = NewSim();
		ProjectileSim client = NewSim();

		uint id = server.Spawn(500, 3, Bullet, new Vector3(1f, 2f, 3f), new Vector3(0.1f, 0.2f, -1f), 0);
		Assert.True(server.TryGet(id, out Projectile spawned));
		client.TrySpawn(id, 500, 3, Bullet, new Vector3(1f, 2f, 3f), new Vector3(0.1f, 0.2f, -1f), 0, out _);

		for (int i = 0; i < 120; i++)
		{
			server.Step(SimConfig.TickDelta);
			client.Step(SimConfig.TickDelta);
		}

		Assert.True(server.TryGet(id, out Projectile a));
		Assert.True(client.TryGet(id, out Projectile b));
		Assert.Equal(a.Position.X, b.Position.X);
		Assert.Equal(a.Position.Y, b.Position.Y);
		Assert.Equal(a.Position.Z, b.Position.Z);
		Assert.NotEqual(spawned.Position, a.Position);
	}

	[Fact]
	public void SplashFallsOffToZeroAtTheRadius()
	{
		ProjectileStats shell = Definitions()[Shell];

		Assert.Equal(90f, shell.SplashDamageAt(0f), 3);
		Assert.Equal(45f, shell.SplashDamageAt(2.5f), 3);
		Assert.Equal(0f, shell.SplashDamageAt(5f));
		Assert.Equal(0f, shell.SplashDamageAt(50f));
		Assert.True(shell.IsExplosive);
	}

	[Fact]
	public void ANonExplosiveRoundHasNoSplash()
	{
		ProjectileStats bullet = Definitions()[Bullet];

		Assert.False(bullet.IsExplosive);
		Assert.Equal(0f, bullet.SplashDamageAt(0f));
	}

	[Fact]
	public void AnUnknownDefinitionDoesNotThrow()
	{
		// Definition ids come off the wire; an id past the catalog is a malformed
		// packet, not a crash.
		ProjectileSim sim = NewSim();

		Assert.True(sim.TrySpawn(1, 0, 1, 200, Vector3.Zero, Vector3.Forward, 0, out _));
		sim.Step(SimConfig.TickDelta);
	}

	[Fact]
	public void ClearEmptiesThePool()
	{
		ProjectileSim sim = NewSim();
		sim.Spawn(0, 1, Bullet, Vector3.Zero, Vector3.Forward, 0);
		sim.Clear();

		Assert.Equal(0, sim.LiveCount);
		Assert.Equal(0, sim.SlotCount);
	}

	[Fact]
	public void RekeyAdoptsAPredictedTracerWithoutMovingIt()
	{
		// A client's own tracer has been flying for a round trip by the time the
		// server's account of the shot arrives. Renaming it keeps it where it is;
		// replacing it would drop it back down its own trajectory
		// (docs/NETCODE.md §4.3).
		ProjectileSim sim = NewSim();
		uint predicted = sim.Spawn(0, 1, Bullet, Vector3.Zero, Vector3.Forward, 0);
		for (int i = 0; i < 6; i++)
		{
			sim.Step(SimConfig.TickDelta);
		}

		Assert.True(sim.TryGet(predicted, out Projectile before));
		Assert.True(sim.Rekey(predicted, 4242));

		Assert.False(sim.TryGet(predicted, out _));
		Assert.True(sim.TryGet(4242, out Projectile after));
		Assert.Equal(before.Position, after.Position);
		Assert.Equal(before.Velocity, after.Velocity);
		Assert.Equal(1, sim.LiveCount);
	}

	[Fact]
	public void RekeyOfAnUnknownProjectileIsNotAnError()
	{
		// The shot was already reaped, or the prediction never happened.
		ProjectileSim sim = NewSim();
		Assert.False(sim.Rekey(99, 100));
	}
}
