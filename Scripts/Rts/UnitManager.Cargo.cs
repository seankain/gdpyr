using System;
using Gdpyr.Agent;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Gdpyr.Sim.Htn;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// Units getting into a vehicle, riding in it and getting out (docs/VEHICLES.md §5):
/// the technical's seats. The rules that need no engine are <see cref="Cargo"/>; this
/// is the half that needs the units, the requests and the physics world.
///
/// A rider stays a unit — same id, same registry entry, same health — so nothing is
/// despawned and nothing respawned: it is alive, hidden, solid to nothing, skipped by
/// the unit loop, and pinned to its vehicle's position every tick
/// (<see cref="ServerCargo"/>). Everything outside the unit loop that asks whether a
/// unit is there asks <see cref="Unit.IsOnField"/>.
///
/// Two requests, in the <see cref="RequestAssist"/> pattern, so a person, a computer
/// strategist and an agent reach the same checks: embark — the units that can ride
/// walk to the vehicle and get in until it is full, the rest stop where they are, and
/// the vehicle waits for them — and unload, which lets everybody out round it.
/// </summary>
public partial class UnitManager
{
	/// <summary>Bytes counted for an embark request, before its ids.</summary>
	private const int EmbarkRequestBytes = 8;

	/// <summary>Bytes counted for an unload request, before its ids.</summary>
	private const int UnloadRequestBytes = 4;

	/// <summary>How far, flat, past a passenger's radius an exit slot may be from the navigation mesh.</summary>
	private const float ExitNavigationSlackMeters = 0.3f;

	/// <summary>How far above or below the navigation mesh an exit slot may be.</summary>
	private const float ExitNavigationClimbMeters = 0.5f;

	/// <summary>
	/// How far above the vehicle's feet the line to an exit slot is drawn, and the ground
	/// under the slot looked for from: over a kerb, under any wall worth the name.
	/// </summary>
	private const float ExitSightHeightMeters = 1f;

	/// <summary>How far below a wreck the ground its riders land on is looked for.</summary>
	private const float WreckGroundProbeMeters = 50f;

	/// <summary>The units one embark request can seat, how far each is from the vehicle, and who got a seat. Pre-sized.</summary>
	private readonly ushort[] _embarkCandidates = new ushort[SimConfig.MaxUnits];
	private readonly float[] _embarkDistances = new float[SimConfig.MaxUnits];
	private readonly ushort[] _embarkBoarders = new ushort[SimConfig.MaxPassengers];
	private readonly ushort[] _embarkLeftovers = new ushort[SimConfig.MaxUnits];

	/// <summary>
	/// The places units were put this tick, and how wide each was: a body moved this
	/// tick may not be in the broadphase yet, so the physics query alone would put the
	/// next one on top of it. Unloads and wrecks share it.
	/// </summary>
	private readonly Vector3[] _exitTaken = new Vector3[SimConfig.MaxUnits];
	private readonly float[] _exitTakenRadius = new float[SimConfig.MaxUnits];
	private int _exitTakenCount;
	private uint _exitTakenTick;

	/// <summary>One ray, one shape query and one capsule per unit kind, made once and changed in place.</summary>
	private PhysicsRayQueryParameters3D _exitRay;
	private PhysicsShapeQueryParameters3D _exitShape;
	private readonly CapsuleShape3D[] _exitCapsules = new CapsuleShape3D[SimConfig.MaxUnitDefinitions];
	private readonly Godot.Collections.Array<Rid> _exitExclude = new();

	/// <summary>Units riding in a vehicle. For the debug HUD; they are in <see cref="LiveUnitCount"/> too.</summary>
	public int RidingCount
	{
		get
		{
			int riding = 0;
			for (int i = 0; i < _ordered.Count; i++)
			{
				riding += _ordered[i].IsRiding ? 1 : 0;
			}
			return riding;
		}
	}

	// ---- requests (docs/VEHICLES.md §5.2) -------------------------------------

	/// <summary>
	/// Asks the server to put <paramref name="unitIds"/> into the vehicle
	/// <paramref name="vehicleId"/>. Applied immediately when this process is the
	/// authority; a client finds out by watching them walk to it.
	/// </summary>
	public void RequestEmbark(int[] unitIds, int vehicleId)
	{
		if (unitIds == null || unitIds.Length == 0)
		{
			return;
		}

		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			RpcId(1, MethodName.ClientEmbark, unitIds, vehicleId);
			net.Stats.RecordSent(EmbarkRequestBytes + (unitIds.Length * 4));
			return;
		}

		ApplyEmbark(net?.LocalPeerId ?? 1, unitIds, vehicleId);
	}

	/// <summary>Asks the server to let everybody out of each of <paramref name="vehicleIds"/>, where it is.</summary>
	public void RequestUnload(int[] vehicleIds)
	{
		if (vehicleIds == null || vehicleIds.Length == 0)
		{
			return;
		}

		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			RpcId(1, MethodName.ClientUnload, vehicleIds);
			net.Stats.RecordSent(UnloadRequestBytes + (vehicleIds.Length * 4));
			return;
		}

		ApplyUnload(net?.LocalPeerId ?? 1, vehicleIds);
	}

	/// <summary>Client -> server, reliable: an order is a decision, not a sample.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientEmbark(int[] unitIds, int vehicleId)
	{
		if (NetworkManager.Instance is not { IsServer: true })
		{
			return;
		}

		NetworkManager.Instance.Stats.RecordReceived(EmbarkRequestBytes + ((unitIds?.Length ?? 0) * 4));
		ApplyEmbark(Multiplayer.GetRemoteSenderId(), unitIds, vehicleId);
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientUnload(int[] vehicleIds)
	{
		if (NetworkManager.Instance is not { IsServer: true })
		{
			return;
		}

		NetworkManager.Instance.Stats.RecordReceived(UnloadRequestBytes + ((vehicleIds?.Length ?? 0) * 4));
		ApplyUnload(Multiplayer.GetRemoteSenderId(), vehicleIds);
	}

	/// <summary>
	/// Puts a unit a scenario placed straight into a vehicle, without the walk
	/// (docs/VEHICLES.md §11): the harness's half of a spawn <c>aboard</c>, as narrow as
	/// <see cref="ServerPlaceUnit"/> and for the same caller. The vehicle's cargo and the
	/// rider's order answer to <paramref name="loaderPeerId"/>, the scenario's strategist
	/// seat, so the orphan rule (<see cref="ServerCargo"/>) does not unload it on its
	/// first tick. Null on success, or what stopped it.
	/// </summary>
	public string ServerSeatPlaced(ushort riderId, ushort vehicleId, int loaderPeerId)
	{
		if (NetworkManager.Instance is { IsClient: true })
		{
			return "this process is not the authority";
		}

		Unit rider = Find(riderId);
		Unit vehicle = Find(vehicleId);
		if (rider is not { IsOnField: true })
		{
			return $"unit {riderId} is not on the field";
		}

		if (vehicle is not { IsOnField: true, HasSeats: true })
		{
			return $"unit {vehicleId} is not a vehicle on the field that carries units";
		}

		if (vehicle.Team != rider.Team)
		{
			return $"unit {vehicleId} is not on unit {riderId}'s side";
		}

		if (!UnitCatalog.CanRide(rider.DefinitionId))
		{
			return $"a {UnitCatalog.NameOf(rider.DefinitionId)} does not ride in anything";
		}

		if (vehicle.Cargo.IsFull)
		{
			return $"{UnitCatalog.NameOf(vehicle.DefinitionId)} {vehicleId} is full ({vehicle.Cargo.Seats} seats)";
		}

		uint tick = NetworkManager.Instance?.Tick ?? 0;
		rider.Order = new UnitOrder
		{
			Kind = OrderKind.Embark,
			Target = vehicle.GlobalPosition,
			Anchor = rider.GlobalPosition,
			TargetOwnerId = OwnerId.ForUnit(vehicle.UnitId),
			Returning = false,
		};
		rider.OrderIssuer = loaderPeerId;
		rider.OrderTick = tick;
		rider.GlobalPosition = vehicle.GlobalPosition;
		vehicle.CargoIssuer = loaderPeerId;
		return Board(rider, vehicle, tick) ? null : $"{UnitCatalog.NameOf(vehicle.DefinitionId)} {vehicleId} refused it";
	}

	/// <summary>Server-side: an embark on behalf of a peer with no client, through the same checks a person's gets.</summary>
	public bool ServerEmbark(int peerId, ReadOnlySpan<int> unitIds, int vehicleId) =>
		NetworkManager.Instance is not { IsClient: true } && ApplyEmbark(peerId, unitIds, vehicleId);

	/// <summary>Server-side: an unload on behalf of a peer with no client.</summary>
	public bool ServerUnload(int peerId, ReadOnlySpan<int> vehicleIds) =>
		NetworkManager.Instance is not { IsClient: true } && ApplyUnload(peerId, vehicleIds);

	/// <summary>
	/// Validates and applies an embark (V-D3, V-D4). Refused unless the sender is a
	/// strategist and the vehicle is a live one of theirs with seats. The candidates
	/// are the distinct live units of theirs on foot that can ride; the free seats are
	/// the vehicle's, less those taken, less those reserved by units already walking to
	/// it that are not in this request — so the same click twice keeps them walking.
	/// With no free seat nothing changes for anybody. Otherwise the nearest board, the
	/// rest stop where they are, and the vehicle holds where it is and waits.
	///
	/// Rejections follow the rule <see cref="ApplyOrder"/> does (NETCODE.md §6.3): what
	/// a client cannot honestly send counts in <see cref="RejectedOrders"/>; what a
	/// round trip can change — dead, riding, gone — is skipped.
	/// </summary>
	private bool ApplyEmbark(int senderPeerId, ReadOnlySpan<int> unitIds, int vehicleId)
	{
		Team team = CombatManager.Instance?.TeamOf(senderPeerId) ?? Team.GroundForce;
		if (team != Team.Strategist || vehicleId <= 0 || vehicleId > ushort.MaxValue)
		{
			RejectedOrders++;
			return false;
		}

		Unit vehicle = Find((ushort)vehicleId);
		if (vehicle == null || !vehicle.IsOnField)
		{
			return false;
		}

		if (vehicle.Team != team || !vehicle.HasSeats)
		{
			RejectedOrders++;
			return false;
		}

		int candidates = GatherRiders(unitIds, vehicle, team);
		if (candidates == 0)
		{
			return false;
		}

		int free = vehicle.Cargo.FreeSeats - CountBoarders(vehicle, _embarkCandidates.AsSpan(0, candidates));
		if (free <= 0)
		{
			// A misclick on a full truck must not stop a squad mid-fight.
			return false;
		}

		int boarding = Cargo.Admit(_embarkCandidates.AsSpan(0, candidates), _embarkDistances.AsSpan(0, candidates), free,
			_embarkBoarders, _embarkLeftovers);
		uint tick = NetworkManager.Instance?.Tick ?? 0;

		for (int i = 0; i < boarding; i++)
		{
			Unit unit = Find(_embarkBoarders[i]);
			unit.Order = new UnitOrder
			{
				Kind = OrderKind.Embark,
				Target = Cargo.StandOff(vehicle.GlobalPosition, unit.GlobalPosition, RadiusOf(vehicle), RadiusOf(unit),
					vehicle.HeightAboveGround),
				Anchor = unit.GlobalPosition,
				TargetOwnerId = OwnerId.ForUnit(vehicle.UnitId),
				Returning = false,
			};
			Reordered(unit, senderPeerId, tick);
		}

		for (int i = 0; i < candidates - boarding; i++)
		{
			Unit unit = Find(_embarkLeftovers[i]);
			unit.Order = UnitOrder.Hold(unit.GlobalPosition);
			Reordered(unit, senderPeerId, tick);
		}

		// The vehicle waits for them where it is — a dropship in the air comes down there.
		// An order given to it while they are on their way is kept for when they are
		// aboard (ApplyOrder), and a standing unload is over. It keeps its gun and any
		// order already waiting.
		vehicle.Order = UnitOrder.Hold(vehicle.GlobalPosition);
		vehicle.OrderIssuer = senderPeerId;
		vehicle.OrderTick = tick;
		vehicle.CargoIssuer = senderPeerId;
		vehicle.NextScanTick = 0;
		ResetPlan(vehicle);
		Squads.Leave(vehicle.UnitId);
		return true;
	}

	/// <summary>
	/// Validates and applies an unload (V-D5): each vehicle named that is a live one of
	/// the sender's with somebody aboard stops where it is and lets them out; units
	/// still walking to it stand down, and an order it was keeping for when they were
	/// aboard is dropped. An empty vehicle is left alone — the button is greyed out, but
	/// its last passenger may have died since the click.
	/// </summary>
	private bool ApplyUnload(int senderPeerId, ReadOnlySpan<int> vehicleIds)
	{
		Team team = CombatManager.Instance?.TeamOf(senderPeerId) ?? Team.GroundForce;
		if (team != Team.Strategist || vehicleIds.Length == 0)
		{
			RejectedOrders++;
			return false;
		}

		uint tick = NetworkManager.Instance?.Tick ?? 0;
		bool unloading = false;
		for (int i = 0; i < vehicleIds.Length; i++)
		{
			int raw = vehicleIds[i];
			if (raw <= 0 || raw > ushort.MaxValue)
			{
				RejectedOrders++;
				continue;
			}

			Unit vehicle = Find((ushort)raw);
			if (vehicle == null || !vehicle.IsOnField)
			{
				continue;
			}

			if (vehicle.Team != team || !vehicle.HasSeats)
			{
				RejectedOrders++;
				continue;
			}

			if (vehicle.Cargo.IsEmpty)
			{
				continue;
			}

			BeginUnload(vehicle, senderPeerId, tick);
			StandDownBoarders(vehicle, senderPeerId, tick);
			unloading = true;
		}

		return unloading;
	}

	/// <summary>The live units of the sender's on foot among <paramref name="unitIds"/> that can ride, once each, and how far each is from the vehicle.</summary>
	private int GatherRiders(ReadOnlySpan<int> unitIds, Unit vehicle, Team team)
	{
		int count = 0;
		for (int i = 0; i < unitIds.Length && count < _embarkCandidates.Length; i++)
		{
			int raw = unitIds[i];
			if (raw <= 0 || raw > ushort.MaxValue)
			{
				RejectedOrders++;
				continue;
			}

			Unit unit = Find((ushort)raw);
			if (unit == null || !unit.IsOnField || unit == vehicle)
			{
				continue;
			}

			if (unit.Team != team || !UnitCatalog.CanRide(unit.DefinitionId))
			{
				RejectedOrders++;
				continue;
			}

			// A client chooses the list, so the same unit may be in it any number of times.
			if (_embarkCandidates.AsSpan(0, count).IndexOf(unit.UnitId) >= 0)
			{
				continue;
			}

			_embarkCandidates[count] = unit.UnitId;
			_embarkDistances[count] = Flat(unit.GlobalPosition, vehicle.GlobalPosition);
			count++;
		}

		return count;
	}

	/// <summary>Units on foot walking to <paramref name="vehicle"/> to get in, leaving out <paramref name="except"/>.</summary>
	private int CountBoarders(Unit vehicle, ReadOnlySpan<ushort> except = default)
	{
		int owner = OwnerId.ForUnit(vehicle.UnitId);
		int count = 0;
		for (int i = 0; i < _ordered.Count; i++)
		{
			Unit unit = _ordered[i];
			if (IsBoarding(unit, owner) && except.IndexOf(unit.UnitId) < 0)
			{
				count++;
			}
		}

		return count;
	}

	private static bool IsBoarding(Unit unit, int vehicleOwnerId) =>
		unit.IsOnField && unit.Order.Kind == OrderKind.Embark && unit.Order.TargetOwnerId == vehicleOwnerId;

	/// <summary>Everybody walking to <paramref name="vehicle"/> holds where they are, under <paramref name="issuer"/>'s word.</summary>
	private void StandDownBoarders(Unit vehicle, int issuer, uint tick)
	{
		int owner = OwnerId.ForUnit(vehicle.UnitId);
		for (int i = 0; i < _ordered.Count; i++)
		{
			Unit unit = _ordered[i];
			if (IsBoarding(unit, owner))
			{
				unit.Order = UnitOrder.Hold(unit.GlobalPosition);
				Reordered(unit, issuer, tick);
			}
		}

		vehicle.BoardersWalking = 0;
		vehicle.HasDeparture = false;
	}

	/// <summary>Sets a vehicle to unload where it is: it stands still, and the cargo pass lets its riders out.</summary>
	private void BeginUnload(Unit vehicle, int issuer, uint tick)
	{
		Vector3 at = vehicle.GlobalPosition;
		vehicle.Order = new UnitOrder
		{
			Kind = OrderKind.Unload,
			Target = at,
			Anchor = at,
			TargetOwnerId = OwnerId.None,
			Returning = false,
		};
		vehicle.HasDeparture = false;
		vehicle.UnloadRetryTick = 0;
		Reordered(vehicle, issuer, tick);
	}

	/// <summary>What every new order does to a unit, as <see cref="ApplyOrder"/> does it: whose, when, a fresh scan and a fresh plan, and out of its squad.</summary>
	private void Reordered(Unit unit, int issuer, uint tick)
	{
		unit.OrderIssuer = issuer;
		unit.OrderTick = tick;
		unit.NextScanTick = 0;
		ResetPlan(unit);
		Suppression.Clear(ref unit.LastSeen);
		Squads.Leave(unit.UnitId);
	}

	// ---- server: the tick (docs/VEHICLES.md §5.3) -----------------------------

	/// <summary>
	/// The part of a unit's tick about vehicles, before any other part of it: true
	/// when that was all of it. A rider does nothing — no plan, scan, shot, throw, step
	/// or resupply. A boarder whose vehicle can still take it gets in once it is in
	/// reach, and that is its tick, so it spends no sight-line ray and cannot fire or
	/// throw from inside; until then it walks to a point beside the vehicle as a unit on
	/// a move does. Registry order decides who takes the last seat when two arrive on
	/// one tick.
	/// </summary>
	private bool SimulateCargo(Unit unit, uint tick)
	{
		if (unit.IsRiding)
		{
			return true;
		}

		if (unit.Order.Kind != OrderKind.Embark)
		{
			return false;
		}

		Unit vehicle = ResolveEmbarkOrder(unit, tick);
		if (vehicle == null)
		{
			return false;
		}

		// A dropship takes nobody until it is down; its boarders wait beside where it is
		// coming down, on the ground under it.
		float vehicleRadius = RadiusOf(vehicle);
		float unitRadius = RadiusOf(unit);
		if (Flight.CanBoard(vehicle.Flight)
			&& Cargo.InReach(vehicle.GlobalPosition, vehicleRadius, unit.GlobalPosition, unitRadius))
		{
			return Board(unit, vehicle, tick);
		}

		unit.Order.Target = Cargo.StandOff(vehicle.GlobalPosition, unit.GlobalPosition, vehicleRadius, unitRadius,
			vehicle.HeightAboveGround);
		return false;
	}

	/// <summary>
	/// The vehicle a boarder's order names, if it can still take it: there, alive, on
	/// foot, its side's, with a free seat, and the order younger than
	/// <see cref="Cargo.BoardTimeoutTicks"/>. Anything else and the boarder stands down
	/// where it is.
	/// </summary>
	private Unit ResolveEmbarkOrder(Unit unit, uint tick)
	{
		int owner = unit.Order.TargetOwnerId;
		Unit vehicle = OwnerId.IsUnit(owner) ? Find(OwnerId.UnitOf(owner)) : null;

		bool valid = vehicle != null && vehicle != unit && vehicle.IsOnField && vehicle.Team == unit.Team
			&& vehicle.HasSeats && !vehicle.Cargo.IsFull && UnitCatalog.CanRide(unit.DefinitionId)
			&& (tick <= unit.OrderTick || tick - unit.OrderTick < (uint)Cargo.BoardTimeoutTicks);
		if (valid)
		{
			return vehicle;
		}

		unit.Order = UnitOrder.Hold(unit.GlobalPosition);
		return null;
	}

	/// <summary>
	/// Puts a unit in its seat: hidden, solid to nothing, out of its squad and its cover,
	/// shooting at nothing — and says so on the agent event stream (<c>unit_embarked</c>).
	/// </summary>
	private bool Board(Unit unit, Unit vehicle, uint tick)
	{
		if (!vehicle.Cargo.TryAdd(unit.UnitId))
		{
			unit.Order = UnitOrder.Hold(unit.GlobalPosition);
			return false;
		}

		unit.RidingIn = vehicle.UnitId;
		unit.Velocity = Vector3.Zero;
		unit.State = UnitStateId.Idle;
		unit.ApplyPresentation();
		ResetPlan(unit);
		Squads.Leave(unit.UnitId);
		unit.HasCoverSpot = false;

		// The shooting target, not the order's, which names the vehicle.
		unit.TargetOwnerId = OwnerId.None;
		unit.AimsHigh = false;
		Suppression.Clear(ref unit.LastSeen);
		unit.Order.Target = vehicle.GlobalPosition;

		AgentEventBus.Emit(AgentEventKind.UnitEmbarked, tick, unit.UnitId, vehicle.UnitId, unit.DefinitionId,
			vehicle.GlobalPosition.X, vehicle.GlobalPosition.Z);
		return true;
	}

	/// <summary>
	/// The cargo pass, once a tick after the units and the structures — whose finish can
	/// still move a vehicle out of a footprint — and before the corpses are reaped and
	/// the snapshot goes out (docs/VEHICLES.md §5.3). Who is walking to which vehicle,
	/// then for every vehicle with seats: let a departed loader's units out, start an
	/// order kept for when everybody was aboard, and let riders out of a vehicle that
	/// is unloading. Last, every rider is put where its vehicle is.
	/// </summary>
	private void ServerCargo(uint tick)
	{
		for (int i = 0; i < _ordered.Count; i++)
		{
			_ordered[i].BoardersWalking = 0;
		}

		for (int i = 0; i < _ordered.Count; i++)
		{
			Unit unit = _ordered[i];
			if (unit.IsOnField && unit.Order.Kind == OrderKind.Embark && OwnerId.IsUnit(unit.Order.TargetOwnerId)
				&& Find(OwnerId.UnitOf(unit.Order.TargetOwnerId)) is { } vehicle)
			{
				vehicle.BoardersWalking++;
			}
		}

		for (int i = 0; i < _ordered.Count; i++)
		{
			Unit vehicle = _ordered[i];
			if (!vehicle.HasSeats || !vehicle.IsOnField)
			{
				continue;
			}

			// Whatever AI is running, or none: a person who loads a squad and leaves must
			// not strand units that never fight and, being alive, never let the side lose.
			if ((!vehicle.Cargo.IsEmpty || vehicle.BoardersWalking > 0) && !IsSeatedStrategist(vehicle.CargoIssuer))
			{
				Orphan(vehicle, tick);
			}

			if (vehicle.HasDeparture && vehicle.BoardersWalking == 0)
			{
				Depart(vehicle, tick);
			}

			// At rest: a technical the tick after it was told, a dropship once it is down.
			if (vehicle.Order.Kind == OrderKind.Unload && tick > vehicle.OrderTick && Flight.CanUnload(vehicle.Flight))
			{
				StepUnload(vehicle, tick);
			}
		}

		PinRiders();
	}

	/// <summary>
	/// Whether a peer can still be the one a vehicle's cargo answers to: on the
	/// strategist side, and a person or a seat an external policy is driving. A computer
	/// strategist never loads anything (V-D9), so a bot seat with no policy on it is a
	/// policy that has gone.
	/// </summary>
	private static bool IsSeatedStrategist(int peerId) =>
		peerId != 0 && (CombatManager.Instance?.TeamOf(peerId) ?? Team.GroundForce) == Team.Strategist
		&& (!BotRoster.IsBot(peerId) || (AgentServer.Instance?.IsAttached(peerId) ?? false));

	/// <summary>A vehicle whose loader has gone: the units on their way stand down, and it unloads where it stands, on nobody's word.</summary>
	private void Orphan(Unit vehicle, uint tick)
	{
		StandDownBoarders(vehicle, issuer: 0, tick);
		if (vehicle.Cargo.IsEmpty || vehicle.Order.Kind == OrderKind.Unload)
		{
			return;
		}

		GD.Print($"[rts] {UnitCatalog.NameOf(vehicle.DefinitionId)} {vehicle.UnitId}: its loader (peer"
			+ $" {vehicle.CargoIssuer}) is no longer a strategist; unloading {vehicle.Cargo.Count}");
		BeginUnload(vehicle, issuer: 0, tick);
	}

	/// <summary>Starts the order a vehicle was keeping for when nobody was walking to it any more, in a squad of its own, as the order would have put it.</summary>
	private void Depart(Unit vehicle, uint tick)
	{
		UnitOrder order = vehicle.Departure;
		vehicle.HasDeparture = false;
		vehicle.Order = order;
		vehicle.NextScanTick = 0;
		ResetPlan(vehicle);

		if (order.Kind == OrderKind.None || vehicle.IsAircraft)
		{
			return;
		}

		_orderedUnits[0] = vehicle.UnitId;
		_orderedStrengths[0] = ForceRatio.Strength(UnitCatalog.CostOf(vehicle.DefinitionId),
			vehicle.HealthPercent / (float)byte.MaxValue);
		Squads.Form(tick, vehicle.OrderIssuer, order.Kind, order.Target,
			order.Kind == OrderKind.Attack ? order.TargetOwnerId : OwnerId.None,
			_orderedUnits.AsSpan(0, 1), _orderedStrengths.AsSpan(0, 1));
	}

	/// <summary>
	/// One tick of an unloading vehicle at rest — the tick after it was told, by which
	/// its own step has stopped it. Everybody who has a clear place goes; anybody who
	/// has none stays aboard and is tried again a scan interval later, while the order
	/// stands. Empty, it holds where it is.
	/// </summary>
	private void StepUnload(Unit vehicle, uint tick)
	{
		if (!vehicle.Cargo.IsEmpty && tick >= vehicle.UnloadRetryTick)
		{
			LetOut(vehicle, tick);
			vehicle.UnloadRetryTick = tick + (uint)SimConfig.UnitTargetRefreshTicks;
		}

		if (vehicle.Cargo.IsEmpty)
		{
			vehicle.Order = UnitOrder.Hold(vehicle.GlobalPosition);
		}
	}

	/// <summary>Lets each rider out, in the order they got in, at the first exit slot that is clear for it.</summary>
	private void LetOut(Unit vehicle, uint tick)
	{
		for (int k = 0; k < vehicle.Cargo.Count;)
		{
			ushort id = vehicle.Cargo[k];
			Unit rider = Find(id);
			if (rider == null || !rider.IsAlive || rider.RidingIn != vehicle.UnitId)
			{
				// Not one of its riders any more. The invariant says this never happens;
				// a hold that keeps a stranger's seat would never empty.
				vehicle.Cargo.Remove(id);
				continue;
			}

			if (TryExitSlot(vehicle, rider, tick, out Vector3 slot))
			{
				GetOut(vehicle, rider, slot, vehicle.OrderIssuer, tick, announce: true);
				continue;
			}

			k++;
		}
	}

	/// <summary>
	/// Takes a rider out of a vehicle and puts it on its feet at <paramref name="at"/>:
	/// shown, solid, still, holding there on <paramref name="issuer"/>'s word — so a
	/// person's claim on it follows whoever let it out — and in no squad until its next
	/// order, as a fresh unit at its door is. Its path is asked for afresh: the one it
	/// had starts where it got in. <paramref name="announce"/> puts a <c>unit_disembarked</c>
	/// on the agent event stream: for every rider that gets out alive, and not for one put
	/// at a wreck only to be killed there.
	/// </summary>
	private void GetOut(Unit vehicle, Unit rider, Vector3 at, int issuer, uint tick, bool announce)
	{
		vehicle?.Cargo?.Remove(rider.UnitId);
		rider.RidingIn = 0;
		rider.GlobalPosition = at;
		rider.Velocity = Vector3.Zero;
		rider.State = UnitStateId.Idle;
		rider.ApplyPresentation();
		rider.Order = UnitOrder.Hold(at);
		rider.OrderIssuer = issuer;
		rider.OrderTick = tick;
		rider.NextScanTick = 0;
		rider.ForceRepath();
		ResetPlan(rider);
		TakeExitSlot(at, RadiusOf(rider), tick);

		if (announce)
		{
			AgentEventBus.Emit(AgentEventKind.UnitDisembarked, tick, rider.UnitId, vehicle?.UnitId ?? 0,
				rider.DefinitionId, at.X, at.Z);
		}
	}

	/// <summary>
	/// Server-side, for <c>CombatManager.ResolveWrecks</c> (docs/VEHICLES.md §5.5): takes
	/// one rider out of a destroyed vehicle. When the wreck is one riders survive, at the
	/// first clear exit slot, and <paramref name="placed"/> is true; otherwise, or with
	/// no clear slot, where the wreck is, on the ground under it. Either way it is shown
	/// and on its feet, and the caller deals it what the wreck does. Null when the id
	/// names no living rider, which is then only taken off the hold.
	/// </summary>
	public Unit EjectFromWreck(Unit vehicle, ushort riderId, bool survivable, uint tick, out bool placed)
	{
		placed = false;
		vehicle?.Cargo?.Remove(riderId);

		Unit rider = Find(riderId);
		if (rider == null || !rider.IsAlive || !rider.IsRiding)
		{
			return null;
		}

		Vector3 wreck = vehicle?.GlobalPosition ?? rider.GlobalPosition;
		if (survivable && vehicle != null && TryExitSlot(vehicle, rider, tick, out Vector3 slot))
		{
			GetOut(vehicle, rider, slot, rider.OrderIssuer, tick, announce: true);
			placed = true;
			return rider;
		}

		GetOut(vehicle, rider, GroundUnder(wreck), rider.OrderIssuer, tick, announce: false);
		return rider;
	}

	/// <summary>
	/// Every rider at its vehicle's position, and its order's target with it. Position
	/// only: nothing reads a rider's velocity, and a copied one would survive into its
	/// first step after it gets out. Fog, the snapshot, the agent views and the
	/// spectator then read a rider at the right place with no code of their own.
	/// </summary>
	private void PinRiders()
	{
		for (int i = 0; i < _ordered.Count; i++)
		{
			Unit rider = _ordered[i];
			if (!rider.IsRiding)
			{
				continue;
			}

			Unit vehicle = Find(rider.RidingIn);
			if (vehicle == null)
			{
				// Its vehicle was freed with it aboard, which Despawn never does. On its
				// feet where it last was, rather than hidden for ever.
				rider.RidingIn = 0;
				rider.ApplyPresentation();
				continue;
			}

			rider.GlobalPosition = vehicle.GlobalPosition;
			rider.Order.Target = vehicle.GlobalPosition;
		}
	}

	// ---- getting out (docs/VEHICLES.md §5.4) ----------------------------------

	/// <summary>
	/// The first of <see cref="Cargo.ExitSlot"/>'s candidates round a vehicle a rider can
	/// be put at — in the field, not taken this tick, in sight of the vehicle, on the
	/// ground, on the navigation mesh and clear of bodies — and where on the ground that
	/// is.
	/// </summary>
	private bool TryExitSlot(Unit vehicle, Unit rider, uint tick, out Vector3 slot)
	{
		float vehicleRadius = RadiusOf(vehicle);
		float riderRadius = RadiusOf(rider);
		int count = Cargo.ExitSlotCount(vehicleRadius, riderRadius);

		for (int ordinal = 0; ordinal < count; ordinal++)
		{
			Vector3 candidate = Cargo.ExitSlot(vehicle.GlobalPosition, vehicle.Yaw, vehicleRadius, riderRadius, ordinal);
			if (!Cargo.InField(candidate) || IsExitTaken(candidate, riderRadius, tick))
			{
				continue;
			}

			if (IsExitClear(vehicle, rider, candidate, out slot))
			{
				return true;
			}
		}

		slot = vehicle.GlobalPosition;
		return false;
	}

	/// <summary>
	/// The four checks of an exit slot that ask the world, cheapest first, beside
	/// <see cref="ValidateSite"/>'s for a structure:
	/// <list type="bullet">
	/// <item>reachable — nothing on the world layer between the vehicle and the slot, a
	/// metre up, so a vehicle parked against a perimeter wall does not put its second
	/// ring through it onto the strip outside, which is clear, on ground and on a
	/// navigation mesh of its own;</item>
	/// <item>ground — under the slot, from that same height rather than from above,
	/// so a slot under a roof is the floor and not the roof; and inside the band a unit
	/// gets in from, so nobody gets out at the foot of a drop;</item>
	/// <item>navigation mesh — within a passenger's own tolerance of it, not a
	/// structure's; skipped, as a site's is, while there is none;</item>
	/// <item>clearance — the passenger's capsule, lifted off the floor, against the
	/// world, units and players, leaving out the vehicle: the map's invisible colliders
	/// are not carved from the navigation mesh.</item>
	/// </list>
	/// </summary>
	private bool IsExitClear(Unit vehicle, Unit rider, Vector3 candidate, out Vector3 ground)
	{
		ground = candidate;
		PhysicsDirectSpaceState3D space = GetTree()?.Root?.World3D?.DirectSpaceState;
		if (space == null)
		{
			return true;
		}

		Vector3 lift = Vector3.Up * ExitSightHeightMeters;
		if (WorldRay(space, vehicle.GlobalPosition + lift, candidate + lift, out _))
		{
			return false;
		}

		if (!WorldRay(space, candidate + lift, candidate - (Vector3.Up * GroundProbeMeters), out ground)
			|| Mathf.Abs(ground.Y - vehicle.GlobalPosition.Y) > CapturePad.VerticalToleranceMeters)
		{
			return false;
		}

		float riderRadius = RadiusOf(rider);
		if (NavigationReady)
		{
			Vector3 walkable = NavigationServer3D.MapGetClosestPoint(GetTree().Root.World3D.NavigationMap, ground);
			if (Flat(walkable, ground) > riderRadius + ExitNavigationSlackMeters
				|| Mathf.Abs(walkable.Y - ground.Y) > ExitNavigationClimbMeters)
			{
				return false;
			}
		}

		return !IsCrowded(space, vehicle, rider, ground);
	}

	/// <summary>Whether anything solid — the world, a unit or a player, the vehicle aside — is where a passenger standing at <paramref name="feet"/> would be.</summary>
	private bool IsCrowded(PhysicsDirectSpaceState3D space, Unit vehicle, Unit rider, Vector3 feet)
	{
		float height = rider.Definition?.HeightMeters ?? 1.8f;
		float radius = RadiusOf(rider);
		byte kind = rider.DefinitionId;

		CapsuleShape3D capsule = kind < _exitCapsules.Length
			? (_exitCapsules[kind] ??= new CapsuleShape3D { Height = height, Radius = radius })
			: new CapsuleShape3D { Height = height, Radius = radius };

		_exitShape ??= new PhysicsShapeQueryParameters3D
		{
			CollisionMask = CollisionLayers.World | CollisionLayers.Units | CollisionLayers.Players,
		};
		_exitShape.Shape = capsule;
		_exitShape.Transform = new Transform3D(Basis.Identity,
			feet + (Vector3.Up * ((height * 0.5f) + PlacementProbeLiftMeters)));
		_exitExclude.Clear();
		_exitExclude.Add(vehicle.GetRid());
		_exitShape.Exclude = _exitExclude;

		return space.IntersectShape(_exitShape, 1).Count > 0;
	}

	/// <summary>A world-layer ray, through the one cached query: true and where, when it hits.</summary>
	private bool WorldRay(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to, out Vector3 point)
	{
		_exitRay ??= new PhysicsRayQueryParameters3D { CollisionMask = CollisionLayers.World };
		_exitRay.From = from;
		_exitRay.To = to;

		Godot.Collections.Dictionary hit = space.IntersectRay(_exitRay);
		if (hit.Count == 0)
		{
			point = to;
			return false;
		}

		point = hit["position"].AsVector3();
		return true;
	}

	/// <summary>The ground under a point — a wreck's — or the point itself when there is none to find.</summary>
	private Vector3 GroundUnder(Vector3 point)
	{
		PhysicsDirectSpaceState3D space = GetTree()?.Root?.World3D?.DirectSpaceState;
		return space != null
			&& WorldRay(space, point + (Vector3.Up * 0.5f), point - (Vector3.Up * WreckGroundProbeMeters), out Vector3 ground)
			? ground
			: point;
	}

	/// <summary>Whether a passenger of <paramref name="radius"/> at <paramref name="point"/> would stand in one put somewhere this tick.</summary>
	private bool IsExitTaken(Vector3 point, float radius, uint tick)
	{
		ForgetExitsBefore(tick);
		for (int i = 0; i < _exitTakenCount; i++)
		{
			float apart = radius + _exitTakenRadius[i];
			if (FlatDistanceSquared(point, _exitTaken[i]) < apart * apart)
			{
				return true;
			}
		}

		return false;
	}

	private void TakeExitSlot(Vector3 point, float radius, uint tick)
	{
		ForgetExitsBefore(tick);
		if (_exitTakenCount < _exitTaken.Length)
		{
			_exitTaken[_exitTakenCount] = point;
			_exitTakenRadius[_exitTakenCount] = radius;
			_exitTakenCount++;
		}
	}

	private void ForgetExitsBefore(uint tick)
	{
		if (_exitTakenTick != tick)
		{
			_exitTakenTick = tick;
			_exitTakenCount = 0;
		}
	}

	private static float RadiusOf(Unit unit) => unit.Definition?.RadiusMeters ?? 0.4f;

	private static float FlatDistanceSquared(Vector3 a, Vector3 b)
	{
		float x = a.X - b.X;
		float z = a.Z - b.Z;
		return (x * x) + (z * z);
	}
}
