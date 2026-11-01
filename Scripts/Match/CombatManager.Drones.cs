using System;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Net;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Match;

/// <summary>
/// FPV drones, the ground force's perk for holding a drone node (docs/CONTROL_POINTS.md
/// §4). The flight rules are engine-free (<see cref="DroneSim"/>, <see cref="DroneStock"/>);
/// this half launches them from a pilot's frame, flies them once a tick, asks the world
/// what each one ran into, sets the warhead off through the same blast every explosive
/// uses, and tells clients where they are.
///
/// Server-authoritative and not predicted: a drone is a server object its pilot steers
/// by looking, as a unit is one the strategist steers by clicking. The pilot's body is
/// given a look-only frame while it flies (<see cref="IsPiloting"/>), on the server and on
/// their own client, as a dead player's is.
/// </summary>
public partial class CombatManager
{
	/// <summary>Seconds a drone must have flown before its pilot's trigger sets it off: not the click they launched it under.</summary>
	private const int DroneArmTicks = SimConfig.TickRate / 2;

	/// <summary>On the server the drones in the air; on a client, the last state message's.</summary>
	private readonly Drone[] _drones = new Drone[DroneRules.MaxInFlight];

	/// <summary>Client-side: each drone's whole seconds of battery, as the last message said.</summary>
	private readonly byte[] _droneSeconds = new byte[DroneRules.MaxInFlight];

	/// <summary>Server-side scratch: the drones one recipient may see, for the state message.</summary>
	private readonly Drone[] _dronesShown = new Drone[DroneRules.MaxInFlight];

	private byte[] _pendingDroneState;

	/// <summary>Client-side: the physics tick the last drone state was applied on, for drawing between messages.</summary>
	public uint DroneStateTick { get; private set; }

	/// <summary>The ground force's drones on the shelf: the server's, or what a client was last told.</summary>
	public DroneStock Drones { get; } = new();

	/// <summary>Strategist units a drone's warhead killed this round, the blast's included. Server-side.</summary>
	public int DroneKills { get; private set; }

	/// <summary>Tanks among them.</summary>
	public int DroneTankKills { get; private set; }

	/// <summary>Drone slots, flying or not: <see cref="DroneAt"/> takes an index below this.</summary>
	public int DroneSlots => _drones.Length;

	public Drone DroneAt(int slot) => slot >= 0 && slot < _drones.Length ? _drones[slot] : default;

	/// <summary>Client-side: a drone's seconds of battery, as last told; the server's own count on the server.</summary>
	public float DroneSecondsLeft(int slot, uint tick)
	{
		if (slot < 0 || slot >= _drones.Length || !_drones[slot].Active)
		{
			return 0f;
		}

		return NetworkManager.Instance is { IsServer: true } ? _drones[slot].SecondsLeft(tick) : _droneSeconds[slot];
	}

	/// <summary>
	/// Whether this player is flying a drone, so their body stands still: the server's
	/// own drones, or a client's last state message. A client finds out a one-way trip
	/// after the server, and its prediction of those ticks is corrected as any
	/// misprediction is.
	/// </summary>
	public bool IsPiloting(int peerId) => PilotedSlot(peerId) >= 0;

	/// <summary>The slot of the drone this player is flying, or -1.</summary>
	public int PilotedSlot(int peerId)
	{
		for (int i = 0; i < _drones.Length; i++)
		{
			if (_drones[i].Active && _drones[i].PilotPeerId == peerId)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>
	/// Server-side: one of a player's frames, as they sent it — before a pilot's body is
	/// given a look-only one. The latest is what steers and detonates their drone when
	/// the drones fly (<see cref="ServerDrones"/>), once a tick whatever the input queue did.
	/// </summary>
	public void NoteDroneInput(int peerId, in InputFrame frame)
	{
		PlayerCombat combat = Find(peerId);
		if (combat == null)
		{
			return;
		}

		combat.DroneYaw = frame.YawRadians;
		combat.DronePitch = frame.PitchRadians;
		combat.DroneButtons = frame.Buttons;
	}

	/// <summary>Server-side, from the player's weapon step: a launch, on the drone key's press.</summary>
	private void ServerDroneLaunch(PlayerCombat combat, in InputContext input, uint tick, bool mounted)
	{
		if (!input.JustPressed(InputButtons.Drone) || combat.Character == null)
		{
			return;
		}

		bool flying = IsPiloting(combat.PeerId);
		if (!DroneSim.MayLaunch(combat.IsAlive, combat.Team == Team.GroundForce, flying, mounted, tick,
			combat.DroneEndedTick, Drones.CanLaunch))
		{
			return;
		}

		int slot = -1;
		for (int i = 0; i < _drones.Length && slot < 0; i++)
		{
			slot = _drones[i].Active ? -1 : i;
		}

		if (slot < 0 || !Drones.TryTake())
		{
			return;
		}

		_drones[slot] = DroneSim.Launch(combat.PeerId, combat.Character.EyePosition, input.Yaw, input.Pitch, tick);
		Vector3 at = _drones[slot].Position;
		GD.Print($"[drone] peer {combat.PeerId} launched at ({at.X:0.0}, {at.Y:0.0}, {at.Z:0.0})"
			+ $" | {Drones.Ready} left, {Drones.HeldNodes} drone nodes held");
	}

	/// <summary>
	/// Server-side, once a tick after the projectiles: the shelf, every drone's flight,
	/// and the state message.
	/// </summary>
	private void ServerDrones(uint tick)
	{
		TickDroneStock();

		for (int i = 0; i < _drones.Length; i++)
		{
			if (_drones[i].Active)
			{
				FlyDrone(i, tick);
			}
		}

		if (tick % SimConfig.DroneStateIntervalTicks == 0)
		{
			BroadcastDroneState(tick);
		}
	}

	/// <summary>The drone nodes the ground force holds, into the shelf.</summary>
	private void TickDroneStock()
	{
		int held = 0;
		for (int i = 0; i < Economy.NodeCount; i++)
		{
			ResourceNode node = Economy.NodeAt(i);
			if (node != null && node.Perk == NodePerk.Drone && node.Capture.IsHeldBy(NodeHolder.GroundForce))
			{
				held++;
			}
		}

		Drones.Tick(held);
	}

	/// <summary>
	/// One drone's tick: lost with its pilot; set off by their trigger once armed; else
	/// turned and flown, and set off on the first thing its chord reaches, when its
	/// battery runs out, or lost off the edge of the world.
	/// </summary>
	private void FlyDrone(int slot, uint tick)
	{
		ref Drone drone = ref _drones[slot];
		PlayerCombat pilot = Find(drone.PilotPeerId);
		if (pilot == null || !pilot.IsAlive || pilot.Team != Team.GroundForce || Match.Phase == RoundPhase.Ended)
		{
			EndDrone(slot, DroneEnd.Lost, tick, drone.Position, null, null, null);
			return;
		}

		if ((pilot.DroneButtons & (ushort)InputButtons.Fire) != 0 && tick >= drone.LaunchTick + DroneArmTicks)
		{
			EndDrone(slot, DroneEnd.Detonated, tick, drone.Position, null, null, null);
			return;
		}

		Vector3 from = DroneSim.Step(ref drone, pilot.DroneYaw, pilot.DronePitch, SimConfig.TickDelta);
		int ownerId = OwnerId.ForPeer(drone.PilotPeerId);
		var chord = new ProjectileSegment(0, ownerId, WeaponCatalog.FpvDrone, from, drone.Position,
			DroneRules.SweepRadiusMeters, origin: from);

		if (FirstContact(chord, ownerId, Team.GroundForce, tick, out Vector3 point, out PlayerCombat victim,
			out Unit unitVictim, out Structure structureVictim))
		{
			drone.Position = point;
			EndDrone(slot, DroneEnd.Impact, tick, point, victim, unitVictim, structureVictim);
			return;
		}

		if (DroneSim.BatteryOut(drone, tick))
		{
			EndDrone(slot, DroneEnd.Battery, tick, drone.Position, null, null, null);
			return;
		}

		if (DroneSim.OutOfBounds(drone.Position, Cargo.FieldHalfExtentMeters))
		{
			EndDrone(slot, DroneEnd.OutOfBounds, tick, drone.Position, null, null, null);
		}
	}

	/// <summary>
	/// A drone stops flying. One that goes off does what a launcher's round does on
	/// impact, with its own round's numbers: the whole of its warhead to what it struck,
	/// and the blast to everything in reach — the strategist's units and structures, and
	/// any player, its pilot included, as every blast does.
	/// </summary>
	private void EndDrone(int slot, DroneEnd end, uint tick, Vector3 point, PlayerCombat victim, Unit unitVictim,
		Structure structureVictim)
	{
		Drone drone = _drones[slot];
		_drones[slot] = default;

		PlayerCombat pilot = Find(drone.PilotPeerId);
		if (pilot != null)
		{
			pilot.DroneEndedTick = Math.Max(tick, 1u);
		}

		if (!DroneSim.Explodes(end))
		{
			GD.Print($"[drone] peer {drone.PilotPeerId}'s drone {end.ToString().ToLowerInvariant()}");
			return;
		}

		ProjectileStats stats = WeaponCatalog.RoundFor(WeaponCatalog.FpvDrone);
		int ownerId = OwnerId.ForPeer(drone.PilotPeerId);
		byte weapon = WeaponCatalog.FpvDrone;
		bool tank = unitVictim != null && unitVictim.DefinitionId == UnitCatalog.Tank;
		int lostBefore = UnitManager.Instance?.UnitsLost ?? 0;

		HitFlags flags = HitFlags.Exploded;
		if (victim != null)
		{
			flags |= Damage(victim, stats.Damage, explosive: true, pilot, ownerId, weapon, tick);
		}
		else if (unitVictim != null)
		{
			flags |= DamageUnit(unitVictim, stats.Damage, explosive: true, stats.VehicleDamageScale, pilot, ownerId,
				weapon, tick);
		}
		else if (structureVictim != null)
		{
			flags |= DamageStructure(structureVictim, stats.Damage, explosive: true, stats.StructureDamageScale, pilot,
				ownerId, weapon, tick);
		}

		flags |= Explode(point, stats, pilot, ownerId, Team.GroundForce, weapon, victim, unitVictim, structureVictim,
			tick);

		int killed = (UnitManager.Instance?.UnitsLost ?? 0) - lostBefore;
		DroneKills += Math.Max(killed, 0);
		if (tank && unitVictim.Health <= 0f)
		{
			DroneTankKills++;
		}

		// What the side's units and the ground bots make of a blast, as a grenade's.
		UnitManager.Instance?.NoteBlast(new FireSource(point, ownerId, weapon, 0), Team.GroundForce, point,
			stats.ExplosionRadiusMeters, FireKind.Hit, tick);
		NoteBlastAtBots(Team.GroundForce, point, stats.ExplosionRadiusMeters, tick);

		Broadcast(MethodName.ServerProjectileHit, ProjectileCodec.EncodeHit(new ProjectileHit
		{
			Id = 0,
			Point = point,
			VictimPeerId = victim?.PeerId
				?? (unitVictim != null ? OwnerId.ForUnit(unitVictim.UnitId) : structureVictim?.ShooterId ?? 0),
			Flags = flags,
		}), point);

		if (!Bootstrap.IsDedicatedServer)
		{
			_view?.Impact(point, flags);
		}

		string what = victim != null ? $"peer {victim.PeerId}"
			: unitVictim != null ? $"{UnitCatalog.NameOf(unitVictim.DefinitionId)} {unitVictim.UnitId}"
			: structureVictim != null ? StructureCatalog.NameOf(structureVictim.Kind)
			: "nothing";
		GD.Print($"[drone] peer {drone.PilotPeerId}'s drone {end.ToString().ToLowerInvariant()} on {what}"
			+ $" after {(tick - drone.LaunchTick) * SimConfig.TickDelta:0.0} s at ({point.X:0.0}, {point.Y:0.0}, {point.Z:0.0})"
			+ $" | {(flags.HasFlag(HitFlags.Killed) ? "killed" : "no kill")} | {killed} units lost"
			+ (tank ? " | tank" : string.Empty));
	}

	/// <summary>
	/// The state message (<see cref="DroneCodec"/>) to every peer: the ground force sees
	/// its own drones wherever they are; a strategist sees one only where the side's
	/// sensors cover it, as a projectile's hit is fogged (<see cref="Broadcast"/>).
	/// </summary>
	private void BroadcastDroneState(uint tick)
	{
		if (!Multiplayer.HasMultiplayerPeer())
		{
			return;
		}

		int[] peers = Multiplayer.GetPeers();
		if (peers.Length == 0)
		{
			return;
		}

		int all = 0;
		int fogged = 0;
		for (int i = 0; i < _drones.Length; i++)
		{
			if (_drones[i].Active)
			{
				_dronesShown[all++] = _drones[i];
			}
		}

		byte[] ground = EncodeDrones(_dronesShown.AsSpan(0, all), tick);

		for (int i = 0; i < _drones.Length; i++)
		{
			if (_drones[i].Active && Visibility.Covers(_drones[i].Position))
			{
				_dronesShown[fogged++] = _drones[i];
			}
		}

		byte[] strategist = fogged == all ? ground : EncodeDrones(_dronesShown.AsSpan(0, fogged), tick);

		int bytes = 0;
		for (int i = 0; i < peers.Length; i++)
		{
			byte[] payload = TeamOf(peers[i]) == Team.Strategist ? strategist : ground;
			RpcId(peers[i], MethodName.ServerDroneState, payload);
			bytes += payload.Length;
		}

		NetworkManager.Instance?.Stats.RecordSent(bytes);
	}

	private byte[] EncodeDrones(ReadOnlySpan<Drone> drones, uint tick)
	{
		var payload = new byte[DroneCodec.SizeFor(drones.Length)];
		DroneCodec.Encode(payload, Drones.Ready, Drones.HeldNodes, Drones.Progress, drones, tick);
		return payload;
	}

	/// <summary>Server -> clients, unreliable: the newest supersedes the rest.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	private void ServerDroneState(byte[] payload)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsClient || payload == null)
		{
			return;
		}

		net.Stats.RecordReceived(payload.Length);
		_pendingDroneState = payload;
	}

	/// <summary>Client-side, in the physics step: the newest state message, if one came.</summary>
	private void ApplyDroneState(uint tick)
	{
		if (_pendingDroneState == null)
		{
			return;
		}

		byte[] payload = _pendingDroneState;
		_pendingDroneState = null;

		Span<Drone> decoded = stackalloc Drone[DroneRules.MaxInFlight];
		Span<byte> seconds = stackalloc byte[DroneRules.MaxInFlight];
		if (!DroneCodec.TryDecode(payload, out int ready, out int held, out float progress, decoded, seconds,
			out int count))
		{
			return;
		}

		Array.Clear(_drones);
		for (int i = 0; i < count; i++)
		{
			_drones[i] = decoded[i];
			_droneSeconds[i] = seconds[i];
		}

		Drones.Apply(ready, held, progress);
		DroneStateTick = tick;
	}

	/// <summary>Every drone grounded and the shelf emptied, for the next round. Both sides.</summary>
	private void ResetDrones()
	{
		Array.Clear(_drones);
		Array.Clear(_droneSeconds);
		Drones.Reset();
		DroneKills = 0;
		DroneTankKills = 0;
		for (int i = 0; i < _ordered.Count; i++)
		{
			_ordered[i].DroneEndedTick = 0;
		}
	}
}
