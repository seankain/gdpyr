using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// Anything with a gun that turns on its own: a barracks' defences, and the
/// pillboxes and towers a builder puts up (docs/NETCODE.md §10.4, §10.5).
///
/// It exists so that one set of rules — who to shoot, when it may fire, how fast
/// it swings, where the round goes — runs every gun that nobody is holding, rather
/// than a copy per thing it is mounted on. What differs between them is only where
/// the gun looks from and where its round leaves from, and that is what the two
/// position members say.
/// </summary>
public interface IGunPost
{
	DefenseTraits Traits { get; }

	Team Team { get; }

	/// <summary>What every round it fires names as its owner.</summary>
	int ShooterId { get; }

	byte WeaponDefinitionId { get; }

	WeaponStats Stats { get; }

	float AccuracyConeRadians { get; }

	/// <summary>
	/// Where range and line of sight to <paramref name="point"/> are measured from:
	/// for a pillbox, the slit in whichever wall faces it, so that its own concrete
	/// is never in the way of its own sight line.
	/// </summary>
	Vector3 EyeToward(Vector3 point);

	/// <summary>Where a round aimed at <paramref name="point"/> leaves from.</summary>
	Vector3 MuzzleToward(Vector3 point);

	/// <summary>The gun's weapon, stepped in place.</summary>
	ref WeaponState Belt { get; }

	/// <summary>The peer it is shooting at, or 0.</summary>
	int TargetPeerId { get; set; }

	uint TargetSinceTick { get; set; }

	uint NextScanTick { get; set; }

	/// <summary>Offsets its first scan, so a row of guns does not scan on the same tick.</summary>
	int ScanStagger { get; }

	/// <summary>Where it is pointed. World angles, in <see cref="Aim"/>'s convention.</summary>
	float AimYaw { get; set; }

	float AimPitch { get; set; }

	/// <summary>Turns the body to the aim angles. Presentation only.</summary>
	void ApplyPose();
}

/// <summary>
/// Every barracks' guns and mortars: who each is shooting at, and the rounds it
/// puts in the air (docs/NETCODE.md §10.4).
///
/// Neither a node nor an autoload. <see cref="UnitManager"/> owns one and ticks it
/// straight after its units, which is the one place in the tick a round can be
/// fired from and still fly on the tick it was fired (docs/IMPLEMENTATION_PLAN.md
/// §M3). The rounds go through <see cref="CombatManager.SpawnProjectile"/> like a
/// unit's do — one pool, one integrator, one spawn message — so a defence costs
/// the wire nothing that a rifleman does not, and a client learns everything it
/// needs about one from the spawn records it already receives.
///
/// Target selection is a unit's, narrowed: the nearest ground-force player in
/// range, held with hysteresis until they leave it, re-scanned every
/// <see cref="SimConfig.UnitTargetRefreshTicks"/> and staggered by index. A gun
/// also has to see them, turn to face them and have had its reaction time; a
/// mortar needs none of that past the laying time, and drops its shell where they
/// were standing rather than where they are going.
/// </summary>
public sealed class DefenseBattery
{
	private readonly List<DefenseMount> _mounts = new();

	/// <summary>Defences on the map.</summary>
	public int Count => _mounts.Count;

	public DefenseMount At(int index) => index >= 0 && index < _mounts.Count ? _mounts[index] : null;

	/// <summary>The defence that fired a round, from the round's owner id; null for anything else.</summary>
	public DefenseMount OwnerOf(int ownerId) => At(OwnerId.DefenseOf(ownerId));

	/// <summary>
	/// Finds the map's defences, once, on every peer: the server to fire them and a
	/// client to point them. Sorted and capped, because the index is what a round
	/// names as its owner and every peer has to derive the same one from the same
	/// scene. Sorted by path, not by name as barracks and heavy guns are: a defence is
	/// authored under the barracks it guards, and two barracks' defences may well
	/// share a name.
	/// </summary>
	public void Collect(SceneTree tree)
	{
		_mounts.Clear();

		foreach (Node node in tree.GetNodesInGroup(DefenseMount.Group))
		{
			if (node is DefenseMount mount)
			{
				_mounts.Add(mount);
			}
		}

		_mounts.Sort((a, b) => string.CompareOrdinal(a.GetPath().ToString(), b.GetPath().ToString()));

		if (_mounts.Count > SimConfig.MaxDefenses)
		{
			GD.PushWarning($"[rts] {_mounts.Count} barracks defences; only the first {SimConfig.MaxDefenses}"
				+ " are addressable on the wire");
			for (int i = SimConfig.MaxDefenses; i < _mounts.Count; i++)
			{
				_mounts[i].Index = -1;
			}
			_mounts.RemoveRange(SimConfig.MaxDefenses, _mounts.Count - SimConfig.MaxDefenses);
		}

		for (int i = 0; i < _mounts.Count; i++)
		{
			_mounts[i].Index = i;
		}
	}

	/// <summary>Stands every defence down, for a new round.</summary>
	public void Reset()
	{
		for (int i = 0; i < _mounts.Count; i++)
		{
			_mounts[i].ResetState();
		}
	}

	/// <summary>
	/// Client-side: points the defence that fired a round along it. The only thing a
	/// peer ever learns about a defence, and all it needs.
	/// </summary>
	public void OnShot(int ownerId, Vector3 direction) => OwnerOf(ownerId)?.PointAlong(direction);

	// ---- server --------------------------------------------------------------

	/// <summary>One server tick for every defence on the map.</summary>
	public void ServerTick(uint tick, CombatManager combat, PhysicsDirectSpaceState3D space)
	{
		if (combat == null)
		{
			return;
		}

		for (int i = 0; i < _mounts.Count; i++)
		{
			Simulate(_mounts[i], tick, combat, space);
		}
	}

	private static void Simulate(DefenseMount mount, uint tick, CombatManager combat,
		PhysicsDirectSpaceState3D space)
	{
		if (mount.Kind == DefenseKind.Mortar)
		{
			PlayerCombat target = Engage(mount, tick, combat, space);
			if (target != null)
			{
				FireMortar(mount, target, tick, combat);
			}
			return;
		}

		StepGun(mount, tick, combat, space);
	}

	/// <summary>
	/// One tick of a direct-fire gun on a post: scan when one is due, keep or drop
	/// the target, swing onto it, and fire when <see cref="DefenseSim.MayFire"/> says
	/// so. A barracks' gun, a pillbox and a tower all run through here.
	/// </summary>
	public static void StepGun(IGunPost post, uint tick, CombatManager combat, PhysicsDirectSpaceState3D space)
	{
		if (combat == null)
		{
			return;
		}

		PlayerCombat target = Engage(post, tick, combat, space);
		if (target != null)
		{
			FireGun(post, target, tick, combat);
		}
	}

	/// <summary>
	/// Stands a gun down: no target, and the belt stepped with the trigger off so
	/// that its cadence stays a function of the ticks. For a post that exists but
	/// may not shoot this tick — a site not yet finished.
	/// </summary>
	public static void Idle(IGunPost post, uint tick)
	{
		post.TargetPeerId = 0;
		Trigger(post, tick, pull: false);
	}

	/// <summary>
	/// The scan and the resolve every post shares: re-acquire when a scan is due,
	/// then hand back the current target if it is still worth shooting at. A null
	/// answer has already stood the trigger down.
	/// </summary>
	private static PlayerCombat Engage(IGunPost post, uint tick, CombatManager combat,
		PhysicsDirectSpaceState3D space)
	{
		if (tick >= post.NextScanTick)
		{
			Acquire(post, tick, combat, space);

			uint stagger = post.NextScanTick == 0 ? (uint)(post.ScanStagger % SimConfig.UnitTargetRefreshTicks) : 0u;
			post.NextScanTick = tick + (uint)SimConfig.UnitTargetRefreshTicks + stagger;
		}

		PlayerCombat target = Resolve(post, combat);
		if (target == null)
		{
			post.TargetPeerId = 0;

			// The belt is still stepped with the trigger off, so its cadence is a
			// function of the ticks and not of when somebody last walked into range.
			Trigger(post, tick, pull: false);
		}

		return target;
	}

	/// <summary>
	/// Keeps the target it has while <see cref="DefenseSim.ShouldKeep"/> says so, and
	/// otherwise takes the nearest candidate <see cref="DefenseSim.CanEngage"/>
	/// allows. The line-of-sight ray is the expensive part, so it is only asked of a
	/// candidate that would otherwise win.
	/// </summary>
	private static void Acquire(IGunPost post, uint tick, CombatManager combat, PhysicsDirectSpaceState3D space)
	{
		DefenseTraits traits = post.Traits;

		PlayerCombat held = post.TargetPeerId != 0 ? combat.Find(post.TargetPeerId) : null;
		if (IsCandidate(post, held))
		{
			Vector3 at = AimPoint(post, held);
			Vector3 eye = post.EyeToward(at);
			bool seen = !traits.NeedsLineOfSight || HasLineOfSight(space, eye, held.Character.EyePosition);
			if (DefenseSim.ShouldKeep(traits, eye.DistanceTo(at), seen))
			{
				return;
			}
		}

		PlayerCombat best = null;
		float bestDistance = float.MaxValue;

		for (int i = 0; i < combat.PlayerCount; i++)
		{
			PlayerCombat player = combat.PlayerAt(i);
			if (!IsCandidate(post, player))
			{
				continue;
			}

			Vector3 at = AimPoint(post, player);
			Vector3 eye = post.EyeToward(at);
			float distance = eye.DistanceTo(at);
			if (distance >= bestDistance || !DefenseSim.CanEngage(traits, distance, lineOfSight: true))
			{
				continue;
			}

			if (traits.NeedsLineOfSight && !HasLineOfSight(space, eye, player.Character.EyePosition))
			{
				continue;
			}

			best = player;
			bestDistance = distance;
		}

		int peer = best?.PeerId ?? 0;
		if (peer != post.TargetPeerId)
		{
			// The reaction is counted from the switch: a gun that swaps to somebody new
			// has to find them before it may fire, exactly as a bot does.
			post.TargetPeerId = peer;
			post.TargetSinceTick = tick;
		}
	}

	/// <summary>
	/// The current target, if it is still one worth having: alive, on the other
	/// side and not beyond the hysteresis ring. Runs every tick, as a unit's does,
	/// because where the target is is what the gun is pointing at.
	/// </summary>
	private static PlayerCombat Resolve(IGunPost post, CombatManager combat)
	{
		if (post.TargetPeerId == 0)
		{
			return null;
		}

		PlayerCombat target = combat.Find(post.TargetPeerId);
		if (!IsCandidate(post, target))
		{
			return null;
		}

		Vector3 at = AimPoint(post, target);
		float distance = post.EyeToward(at).DistanceTo(at);
		return DefenseSim.ShouldKeep(post.Traits, distance, lineOfSight: true) ? target : null;
	}

	/// <summary>
	/// A gun aims at the chest, which is what a hit test is most likely to agree
	/// with; a mortar at the ground under the feet, which is what a shell has to hit
	/// to go off beside somebody rather than sail past their shoulder.
	/// </summary>
	private static Vector3 AimPoint(IGunPost post, PlayerCombat player) =>
		post.Traits.Kind == DefenseKind.Mortar ? player.Character.SimPosition : player.Character.Hitbox.Center;

	private static bool IsCandidate(IGunPost post, PlayerCombat player) =>
		player?.Character != null && player.IsAlive && !player.AwaitingRole && player.Team != post.Team;

	private static void FireGun(IGunPost post, PlayerCombat target, uint tick, CombatManager combat)
	{
		Vector3 chest = target.Character.Hitbox.Center;
		Vector3 muzzle = post.MuzzleToward(chest);
		byte weaponId = post.WeaponDefinitionId;

		ProjectileStats projectile = weaponId < WeaponCatalog.Projectiles.Length
			? WeaponCatalog.Projectiles[weaponId]
			: default;

		Vector3 lead = UnitBrain.Lead(muzzle, chest, target.Character.Velocity, projectile.MuzzleVelocity);
		Aim.Angles(lead - muzzle, out float wantYaw, out float wantPitch);

		float yaw = post.AimYaw;
		float pitch = post.AimPitch;
		float error = DefenseSim.Slew(ref yaw, ref pitch, wantYaw, wantPitch, post.Traits);
		post.AimYaw = yaw;
		post.AimPitch = pitch;
		post.ApplyPose();

		bool pull = DefenseSim.MayFire(post.Traits, tick, post.TargetSinceTick, error);
		if (!Trigger(post, tick, pull))
		{
			return;
		}

		// Along the barrel, not along the line to the target: a gun still swinging
		// the last degree onto somebody fires where it is pointed.
		Vector3 direction = Spread.Apply(Aim.Direction(post.AimYaw, post.AimPitch), post.AccuracyConeRadians,
			Spread.Seed(post.ShooterId, weaponId, post.Belt.ShotIndex));

		// No lag compensation, as for a unit: nobody's latency is owed.
		combat.SpawnProjectile(tick, post.ShooterId, weaponId, muzzle, direction, catchUpTicks: 0);
	}

	private static void FireMortar(DefenseMount mount, PlayerCombat target, uint tick, CombatManager combat)
	{
		bool pull = DefenseSim.MayFire(mount.Traits, tick, mount.TargetSinceTick, aimError: 0f);
		if (!Trigger(mount, tick, pull))
		{
			return;
		}

		byte weaponId = mount.WeaponDefinitionId;
		ProjectileStats projectile = weaponId < WeaponCatalog.Projectiles.Length
			? WeaponCatalog.Projectiles[weaponId]
			: default;

		// Where they are standing now, give or take the scatter. No lead: the shell is
		// seconds in the air, and a mortar that led would be a mortar nobody could
		// walk out from under. What it punishes is staying put.
		Vector3 muzzle = mount.MuzzlePosition;
		Vector3 aim = target.Character.SimPosition
			+ Spread.Disc(mount.ScatterMeters, Spread.Seed(mount.ShooterId, weaponId, mount.Weapon.ShotIndex));

		if (!MortarSolver.TrySolve(muzzle, aim, projectile, out Vector3 direction, out _))
		{
			// Scattered out of reach, or the target stepped inside the floor between
			// the scan and the shot. The shell was never loaded; the next one will be
			// laid on wherever they are then.
			return;
		}

		mount.PointAlong(direction);
		combat.SpawnProjectile(tick, mount.ShooterId, weaponId, muzzle, direction, catchUpTicks: 0);
	}

	/// <summary>
	/// Steps a defence's weapon over a synthesized frame, exactly as a unit's is
	/// (<c>UnitManager.ServerFire</c>): the trigger held and reported as a fresh
	/// press, so that both fire modes read it, and the cadence left to
	/// <see cref="WeaponState.NextFireTick"/>. Neither defence weapon has a magazine,
	/// so the one thing that can never come out of this is a reload.
	/// </summary>
	private static bool Trigger(IGunPost post, uint tick, bool pull)
	{
		InputButtons buttons = pull ? InputButtons.Fire : InputButtons.None;
		var frame = InputFrame.Create(tick, 0f, 0f, post.AimYaw, post.AimPitch, buttons);
		var context = new InputContext(frame, (ushort)InputButtons.None, SimConfig.TickDelta);

		return WeaponSim.Step(ref post.Belt, post.Stats, context, tick) == WeaponAction.Fire;
	}

	private static bool HasLineOfSight(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to)
	{
		if (space == null)
		{
			return true;
		}

		var query = PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.World);
		return space.IntersectRay(query).Count == 0;
	}
}
