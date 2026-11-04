using System;
using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// The vehicle kiosk (docs/GROUND_VEHICLES.md §6.6, GV-D13): the map's
/// <see cref="VehicleKiosk"/>, each kind's <see cref="KioskTimer"/>, the pads' check at the
/// start, a request through the one validator (<see cref="KioskRules.Check"/>) and the
/// vehicle put on its pad, a vehicle left alone taken away, and the kiosk's line for each
/// kind that the vehicle message's header carries (§7.1).
///
/// The server is the authority on all of it. A client finds the kiosk too — for the use
/// key's reach, the prompt and the panel — and is told the rest by the header.
/// </summary>
public partial class CrewVehicleManager
{
	/// <summary>How much bigger than the hull's boxes a pad must be clear [×] (§6.6).</summary>
	private const float PadClearanceScale = 1.2f;

	/// <summary>How far below a pad's marker its ground must be [m] (§6.6).</summary>
	private const float PadGroundProbeMeters = 0.6f;

	/// <summary>How far above the marker the ray down for the pad's ground starts, so a marker on the floor finds it [m].</summary>
	private const float PadGroundLiftMeters = 0.05f;

	/// <summary>
	/// Physics frames after the kiosk is found before its pads are checked: a map's CSG
	/// builds its collision on the frame after it enters the tree.
	/// </summary>
	private const ulong PadCheckDelayFrames = 2;

	private VehicleKiosk _kioskNode;
	private bool _kioskScanned;
	private ulong _kioskFoundFrame;
	private bool _padsChecked;
	private readonly KioskTimer[] _timers = new KioskTimer[CrewVehicleCodec.Kinds];
	private readonly string[] _padFailures = new string[CrewVehicleCodec.Kinds];
	private readonly Dictionary<int, uint> _lastKioskRequest = new();
	private byte _askedKind;

	/// <summary>Whether this process found a vehicle kiosk on the map.</summary>
	public bool HasKiosk
	{
		get
		{
			EnsureKiosk();
			return _kioskNode != null;
		}
	}

	/// <summary>The kiosk this process found, or null. For the checks.</summary>
	public VehicleKiosk Kiosk
	{
		get
		{
			EnsureKiosk();
			return _kioskNode;
		}
	}

	/// <summary>Server: whether the pads have been checked (§6.6). Until then the kiosk offers nothing.</summary>
	public bool PadsChecked => _padsChecked;

	/// <summary>Server: why a kind's pad failed its check, or null when it passed or has not been checked.</summary>
	public string PadFailure(byte kind) => kind < _padFailures.Length ? _padFailures[kind] : null;

	/// <summary>Server: a kind's timer, as it stands. For the checks and the log.</summary>
	public KioskTimer TimerOf(byte kind) => kind < _timers.Length ? _timers[kind] : default;

	/// <summary>Server: requests the kiosk has answered since this process started, and how many were taken.</summary>
	public int KioskRequests { get; private set; }

	public int KioskTaken { get; private set; }

	/// <summary>Server: vehicles taken away because they were left alone (GV-D13).</summary>
	public int Abandoned { get; private set; }

	/// <summary>This process's player: the last answer the kiosk gave it, and how many it has had.</summary>
	public KioskRefusal LastKioskAnswer { get; private set; }

	public int KioskAnswers { get; private set; }

	/// <summary>
	/// Flat distance from <paramref name="at"/> to the kiosk's footprint (§5.3), what the
	/// use key's reach and a request are measured by; infinite on a map without one.
	/// </summary>
	public float KioskDistance(Vector3 at)
	{
		EnsureKiosk();
		return _kioskNode != null ? _kioskNode.FootprintDistance(at) : float.PositiveInfinity;
	}

	/// <summary>The kiosk's lines, one per kind, as this process has them: computed on the authority, told to a client.</summary>
	public ReadOnlySpan<KioskLine> KioskLines => _kiosk;

	/// <summary>
	/// Finds the map's kiosk, once the map is up (the arrangement <see cref="EmplacementManager"/>
	/// keeps for the lockers): the first in the group by name, upright. The plan has one
	/// kiosk (§15); a second is left out with a warning, and one a map scaled or tilted is
	/// refused, as a locker is.
	/// </summary>
	private void EnsureKiosk()
	{
		if (_kioskScanned || GetTree()?.CurrentScene == null)
		{
			return;
		}

		_kioskScanned = true;
		var found = new List<VehicleKiosk>();
		foreach (Node node in GetTree().GetNodesInGroup(VehicleKiosk.Group))
		{
			if (node is not VehicleKiosk kiosk)
			{
				continue;
			}

			if (!kiosk.IsUpright)
			{
				GD.PushError($"[kiosk] vehicle kiosk {kiosk.Name} is scaled or tilted; its reach is measured from an upright"
					+ " 1.2 x 0.5 m footprint, so it is left out");
				continue;
			}

			found.Add(kiosk);
		}

		found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
		if (found.Count > 1)
		{
			GD.PushWarning($"[kiosk] {found.Count} vehicle kiosks; only {found[0].Name} hands out vehicles");
		}

		_kioskNode = found.Count > 0 ? found[0] : null;
		_kioskFoundFrame = Engine.GetPhysicsFrames();
		for (byte kind = 0; kind < _timers.Length; kind++)
		{
			_timers[kind] = CrewVehicleCatalog.Definition(kind)?.ToKioskTimer() ?? new KioskTimer(0, 0);
		}

		if (_kioskNode != null)
		{
			GD.Print($"[kiosk] {_kioskNode.Name} at ({_kioskNode.GlobalPosition.X:0.00}, {_kioskNode.GlobalPosition.Z:0.00})");
		}
	}

	// ---- the server's tick ------------------------------------------------------------

	/// <summary>
	/// The kiosk's part of the server's tick, after every vehicle has stepped: the pads
	/// checked once the map's collision is in; once a second, every vehicle left alone
	/// for a minute taken away (GV-D13); then the kiosk's line for each kind.
	/// </summary>
	private void ServerKiosk(uint tick)
	{
		EnsureKiosk();
		EnsurePadsChecked();

		if (tick % SimConfig.TickRate == 0)
		{
			RemoveAbandoned(tick);
		}

		RefreshKioskLines(tick);
	}

	private void EnsurePadsChecked()
	{
		if (_padsChecked || _kioskNode == null || Engine.GetPhysicsFrames() < _kioskFoundFrame + PadCheckDelayFrames
			|| _kioskNode.GetWorld3D()?.DirectSpaceState is not { } space)
		{
			return;
		}

		_padsChecked = true;
		for (byte kind = 0; kind < _timers.Length; kind++)
		{
			string failure = CheckPad(space, kind);
			_padFailures[kind] = failure;
			_timers[kind].SetDisabled(failure != null);
			if (failure != null)
			{
				GD.PushError($"[kiosk] the {CrewVehicleCatalog.NameOf(kind)}'s pad failed its check: {failure};"
					+ " the kiosk does not offer it");
			}
			else
			{
				GD.Print($"[kiosk] the {CrewVehicleCatalog.NameOf(kind)}'s pad is clear");
			}
		}
	}

	/// <summary>
	/// A pad's check at the start (§6.6): ground within 0.6 m below its marker, and the
	/// kind's hull boxes, each 1.2 times its size, clear of the world and every vehicle
	/// where the vehicle would stand. Null when it passes; why, when it does not.
	/// </summary>
	private string CheckPad(PhysicsDirectSpaceState3D space, byte kind)
	{
		if (CrewVehicleCatalog.Definition(kind) is not { } definition)
		{
			return "no definition for the kind";
		}

		if (!_kioskNode.TryPad(kind, out Transform3D pad))
		{
			return $"no {VehicleKiosk.PadName(kind)} marker under {_kioskNode.Name}";
		}

		if (!TryPadPose(space, kind, definition, pad, out CrewPose pose))
		{
			return $"no ground within {PadGroundProbeMeters} m below ({pad.Origin.X:0.0}, {pad.Origin.Y:0.0}, {pad.Origin.Z:0.0})";
		}

		HullBox[] hull = definition.ToHull();
		var box = new BoxShape3D();
		var query = new PhysicsShapeQueryParameters3D
		{
			Shape = box,
			CollisionMask = CollisionLayers.World | CollisionLayers.Vehicles,
		};

		for (int i = 0; i < hull.Length; i++)
		{
			box.Size = hull[i].HalfExtents * 2f * PadClearanceScale;
			query.Transform = new Transform3D(pose.Basis, pose.ToWorld(hull[i].Center));
			if (space.IntersectShape(query, 1).Count > 0)
			{
				return $"hull box {i} ×{PadClearanceScale} is not clear of the world and the vehicles";
			}
		}

		return null;
	}

	/// <summary>Where a vehicle of <paramref name="kind"/> stands on its pad: on the ground under the marker, facing its −Z.</summary>
	private bool TryPadPose(PhysicsDirectSpaceState3D space, byte kind, CrewVehicleDefinition definition,
		in Transform3D pad, out CrewPose pose)
	{
		Vector3 from = pad.Origin + (Vector3.Up * PadGroundLiftMeters);
		if (!WorldRay(space, from, pad.Origin + (Vector3.Down * PadGroundProbeMeters), out Vector3 ground))
		{
			pose = default;
			return false;
		}

		float ride = definition.VehicleKind == CrewVehicleKind.FourByFour
			? definition.ToFourByFourTraits().RideHeightMeters
			: 0f;
		pose = new CrewPose(KioskRules.PadOrigin(definition.VehicleKind, ground, ride), KioskRules.PadYaw(pad.Basis.Z));
		return true;
	}

	/// <summary>Kiosk vehicles of a kind on the field and not wrecked: what its cap counts (GV-D13).</summary>
	private int AliveFromKiosk(byte kind)
	{
		int alive = 0;
		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			if (VehicleAt(slot) is { FromKiosk: true, Destroyed: false } vehicle && (byte)vehicle.Kind == kind)
			{
				alive++;
			}
		}

		return alive;
	}

	/// <summary>Server: a kind's status now — nothing offered before the pads are checked, or on a map without a kiosk.</summary>
	public KioskStatus StatusOf(byte kind, uint tick) =>
		_kioskNode == null || !_padsChecked || kind >= _timers.Length
			? KioskStatus.Disabled
			: _timers[kind].Status(tick, AliveFromKiosk(kind));

	private void RefreshKioskLines(uint tick)
	{
		for (byte kind = 0; kind < _kiosk.Length; kind++)
		{
			KioskStatus status = StatusOf(kind, tick);
			_kiosk[kind] = new KioskLine(status, status == KioskStatus.Counting ? _timers[kind].SecondsLeft(tick) : 0);
		}
	}

	/// <summary>
	/// A kiosk vehicle lost (GV-D13): its kind's timer starts, once. From the hit that
	/// wrecked it (<c>CombatManager.DamageCrewVehicle</c>), and from its removal when it was
	/// left alone; one the console or a scenario put down is nobody's loss.
	/// </summary>
	public void NoteLost(CrewVehicle vehicle, uint tick)
	{
		if (vehicle == null || !vehicle.FromKiosk || vehicle.LossCounted || NetworkManager.Instance is { IsClient: true })
		{
			return;
		}

		vehicle.LossCounted = true;
		byte kind = (byte)vehicle.Kind;
		if (kind < _timers.Length)
		{
			_timers[kind].OnLost(tick);
			GD.Print($"[kiosk] the {vehicle.KindName} is lost: the next is ready in {_timers[kind].SecondsLeft(tick)} s");
		}

		RefreshKioskLines(tick);
	}

	/// <summary>
	/// Every vehicle left alone for a minute — nobody in it and nobody of the ground force
	/// within 15 m at every check — taken away (GV-D13), a kiosk vehicle's kind's timer
	/// started as by a loss. Every vehicle, the console's too: it holds a slot on the wire.
	/// </summary>
	private void RemoveAbandoned(uint tick)
	{
		for (int slot = 0; slot < _vehicles.Length; slot++)
		{
			CrewVehicle vehicle = VehicleAt(slot);
			if (vehicle == null || vehicle.Destroyed)
			{
				continue;
			}

			float nearest = NearestGroundForce(vehicle.Pose.Position);
			if (!vehicle.Abandon.Check(tick, vehicle.Aboard, nearest))
			{
				continue;
			}

			GD.Print($"[kiosk] the {vehicle.KindName} in slot {slot} was left alone for"
				+ $" {KioskRules.AbandonTicks / SimConfig.TickRate} s: taken away");
			Abandoned++;
			NoteLost(vehicle, tick);
			Remove(slot);
		}
	}

	/// <summary>How far the nearest living player of the ground force is from <paramref name="at"/>; infinite with none.</summary>
	private static float NearestGroundForce(Vector3 at)
	{
		CombatManager combat = CombatManager.Instance;
		float nearest = float.PositiveInfinity;
		for (int i = 0; combat != null && i < combat.PlayerCount; i++)
		{
			PlayerCombat player = combat.PlayerAt(i);
			if (player?.Character == null || !player.IsAlive || player.Team != Team.GroundForce)
			{
				continue;
			}

			nearest = Mathf.Min(nearest, player.Character.SimPosition.DistanceTo(at));
		}

		return nearest;
	}

	/// <summary>
	/// A new round (§6.6, from <c>CombatManager.RestartRound</c>, which the agent API's
	/// reset also goes through): every vehicle off the field and both kinds ready; the
	/// pads' verdicts kept.
	/// </summary>
	public void ResetRound(uint tick)
	{
		ClearAll();
		for (int kind = 0; kind < _timers.Length; kind++)
		{
			_timers[kind].Reset();
		}

		_lastKioskRequest.Clear();
		if (NetworkManager.Instance is not { IsClient: true })
		{
			RefreshKioskLines(tick);
		}
	}

	// ---- a request ----------------------------------------------------------------------

	/// <summary>
	/// The one way a vehicle is taken at the kiosk (§6.6): the asker and the kind through
	/// <see cref="KioskRules.Check"/>, and on success the vehicle on its kind's pad, facing
	/// its marker's −Z, at rest. A person's panel or console (<see cref="RequestVehicle"/>),
	/// and the agent API's <c>vehicle</c> op (GV7), all come here and are refused alike;
	/// every request is a <c>vehicle_requested</c> event. Server only; a client is refused
	/// as if dead, having no say.
	///
	/// Every wire slot taken — only the console's vehicles can do that, the kiosk's cap
	/// being two of four — reads as the kind being out.
	/// </summary>
	public bool TryRequest(PlayerCombat combat, byte kind, uint tick, out KioskRefusal refusal)
	{
		refusal = KioskRefusal.Dead;
		if (combat?.Character == null || NetworkManager.Instance is { IsClient: true })
		{
			return false;
		}

		EnsureKiosk();
		EnsurePadsChecked();

		CrewVehicleDefinition definition = CrewVehicleCatalog.Definition(kind);
		bool known = definition != null && kind < _timers.Length;
		KioskStatus status = known ? StatusOf(kind, tick) : KioskStatus.Disabled;
		if (status == KioskStatus.Ready && FreeSlot() < 0)
		{
			status = KioskStatus.Out;
		}

		CrewPose pose = default;
		bool padClear = known && _kioskNode != null && _kioskNode.TryPad(kind, out Transform3D pad)
			&& _kioskNode.GetWorld3D()?.DirectSpaceState is { } space
			&& TryPadPose(space, kind, definition, pad, out pose)
			&& !IsBlocked(space, definition, pose.Position, pose.Yaw);

		// Dead is the ground force's own: down on the field. A strategist, or somebody still
		// choosing a side, is not on the ground force — whatever their health reads, which
		// the role menu zeroes (PlayerCombat.HoldForRoleChoice).
		EmplacementManager emplacements = EmplacementManager.Instance;
		int peer = combat.PeerId;
		var situation = new KioskSituation(
			!combat.IsDowned,
			combat.Team == Team.GroundForce && !combat.AwaitingRole,
			KioskDistance(combat.Character.SimPosition),
			IsSeated(peer),
			emplacements != null && emplacements.TryMounted(peer, out _),
			emplacements?.CarryOf(peer) ?? CarryKind.None,
			CombatManager.Instance?.IsPiloting(peer) ?? false,
			known,
			status,
			padClear,
			tick,
			_lastKioskRequest.GetValueOrDefault(peer));

		refusal = KioskRules.Check(situation);

		// Every request is paced, honoured or not; zero is "never", so tick 0 is kept as 1.
		_lastKioskRequest[peer] = Math.Max(tick, 1u);
		KioskRequests++;
		AgentEventBus.Emit(AgentEventKind.VehicleRequested, tick, peer, kind, (int)refusal);

		if (refusal != KioskRefusal.None)
		{
			GD.Print($"[kiosk] peer {peer} asked for a {CrewVehicleCatalog.NameOf(kind)}: refused"
				+ $" ({KioskRules.CodeOf(refusal)})");
			return false;
		}

		int slot = Spawn(kind, pose.Position, pose.Yaw, peer);
		if (VehicleAt(slot) is not { } vehicle)
		{
			// FreeSlot was asked above, on this tick: not reached.
			refusal = KioskRefusal.Out;
			return false;
		}

		vehicle.FromKiosk = true;
		_timers[kind].OnSpawned(tick);
		KioskTaken++;
		RefreshKioskLines(tick);
		GD.Print($"[kiosk] peer {peer} took a {vehicle.KindName}: slot {slot} on its pad");
		return true;
	}

	/// <summary>
	/// The local player asks the kiosk for <paramref name="kind"/>: the panel's <c>1</c> and
	/// <c>2</c>, and the console's <c>kiosk</c>. Answered on the spot when this process is
	/// the authority — true, with the answer — and by <c>ServerKioskResult</c> a round trip
	/// later on a client. Either way the answer goes to the panel.
	/// </summary>
	public bool RequestVehicle(byte kind, out KioskRefusal refusal)
	{
		refusal = KioskRefusal.Dead;
		if (CombatManager.Instance?.Local is not { } local)
		{
			return false;
		}

		_askedKind = kind;
		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			RpcId(1, MethodName.ClientRequestVehicle, kind);
			return false;
		}

		TryRequest(local, kind, net?.Tick ?? 0, out refusal);
		Answered(refusal);
		return true;
	}

	/// <summary>Client → server, reliable (§7.2): a vehicle asked for at the kiosk.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientRequestVehicle(byte kind)
	{
		if (NetworkManager.Instance is not { IsServer: true } net)
		{
			return;
		}

		int sender = Multiplayer.GetRemoteSenderId();
		if (CombatManager.Instance?.Find(sender) is not { } combat)
		{
			return;
		}

		TryRequest(combat, kind, net.Tick, out KioskRefusal refusal);
		RpcId(sender, MethodName.ServerKioskResult, (byte)refusal);
	}

	/// <summary>Server → the requester alone, reliable (§7.2): taken, or why not.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerKioskResult(byte refusal)
	{
		if (NetworkManager.Instance is not { IsClient: true })
		{
			return;
		}

		Answered(KioskRules.TryRead(refusal, out KioskRefusal read) ? read : KioskRefusal.Invalid);
	}

	private void Answered(KioskRefusal refusal)
	{
		LastKioskAnswer = refusal;
		KioskAnswers++;
		CombatManager.Instance?.AnswerKiosk(_askedKind, refusal);
	}

	/// <summary>
	/// The kiosk's part of the debug HUD's <c>vehicles</c> row (§8.5): <c>4x4 out · helicopter
	/// 0:42</c>, and on the authority why a pad failed.
	/// </summary>
	private string KioskDebugLine()
	{
		if (!HasKiosk)
		{
			return "no kiosk";
		}

		string line = KioskHud.DebugLine(_kiosk);
		for (byte kind = 0; kind < _padFailures.Length; kind++)
		{
			if (_padFailures[kind] != null)
			{
				line += $" · {CrewVehicleCatalog.NameOf(kind)} pad failed";
			}
		}

		return line;
	}
}
