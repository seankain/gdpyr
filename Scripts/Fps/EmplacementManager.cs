using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// Fifth autoload: the ground force's heavy guns and mortar launchers and the cans
/// that feed them (docs/IMPLEMENTATION_PLAN.md §M5), and the weapon lockers at their spawn
/// (docs/LOADOUT.md L-D8) — everything the use key does. A press beside or inside a
/// crew vehicle is resolved here too, by the same table, and handed to
/// <see cref="CrewVehicleManager"/> to act on (docs/GROUND_VEHICLES.md §5.3).
///
/// The guns and the cans are map nodes; this owns what is happening to them. The
/// server is the only authority, as everywhere else: it reads the use bit out of
/// the input frames it already has, decides what the press meant
/// (<see cref="EmplacementSim"/>, engine-free and tested) and tells every client
/// about each transition on a reliable message.
///
/// **Transitions are not predicted.** Every other player action here is —
/// movement, weapons, tracers — but those happen sixty times a second and this
/// happens four times a round. What a mount costs is one round trip before the
/// gun turns, which is the same latency an RTS order carries and which the plan is
/// explicit about being imperceptible (§2); what predicting it would cost is a
/// client that has mounted a gun the server never gave it and no message to tell
/// it otherwise, because a rejection is not a transition. Firing the gun *is*
/// predicted, through the same <see cref="WeaponSim"/> path a rifle uses
/// (docs/NETCODE.md §10.2).
///
/// Like the other managers it never ticks itself: <see cref="PlayerManager"/>
/// drives it.
/// </summary>
public partial class EmplacementManager : Node
{
	/// <summary>Bytes counted for one gun's state RPC, for the bandwidth HUD.</summary>
	private const int GunStateBytes = 32;

	/// <summary>Bytes counted for one can's state RPC.</summary>
	private const int CanStateBytes = 24;

	public static EmplacementManager Instance { get; private set; }

	private readonly List<Emplacement> _guns = new();
	private readonly List<AmmoCan> _cans = new();
	private readonly List<WeaponLocker> _lockers = new();

	/// <summary>peer -> index, maintained on every transition so a lookup is not a scan.</summary>
	private readonly Dictionary<int, int> _carriedGun = new();
	private readonly Dictionary<int, int> _carriedCan = new();
	private readonly Dictionary<int, int> _mounted = new();

	private bool _started;

	/// <summary>Heavy guns on the map. For the debug HUD.</summary>
	public int GunCount => _guns.Count;

	/// <summary>Cans on the map, spent or not.</summary>
	public int CanCount => _cans.Count;

	/// <summary>Weapon lockers on the map.</summary>
	public int LockerCount => _lockers.Count;

	/// <summary>
	/// The weapon locker whose footprint is nearest <paramref name="at"/>, for a ground
	/// bot sent to fetch a launcher (docs/HTN_BOTS.md §5.1): its origin and which way its
	/// racks face (<see cref="LockerSupply"/>). False on a map without one.
	/// </summary>
	public bool TryNearestLocker(Vector3 at, out Vector3 position, out float yaw)
	{
		position = Vector3.Zero;
		yaw = 0f;
		float best = float.PositiveInfinity;
		for (int i = 0; i < _lockers.Count; i++)
		{
			WeaponLocker locker = _lockers[i];
			float d = LockerSupply.FootprintDistance(at, locker.GlobalPosition, locker.Yaw);
			if (d < best)
			{
				best = d;
				position = locker.GlobalPosition;
				yaw = locker.Yaw;
			}
		}

		return _lockers.Count > 0;
	}

	/// <summary>
	/// Flat distance from <paramref name="at"/> to the nearest locker's footprint
	/// (docs/LOADOUT.md §4.4), what a kit request is measured by; infinite on a map
	/// without one.
	/// </summary>
	public float LockerDistance(Vector3 at)
	{
		EnsureStarted();
		float best = float.PositiveInfinity;
		for (int i = 0; i < _lockers.Count; i++)
		{
			best = Mathf.Min(best, LockerSupply.FootprintDistance(at, _lockers[i].GlobalPosition, _lockers[i].Yaw));
		}

		return best;
	}

	/// <summary>Guns with somebody behind them right now.</summary>
	public int MountedGuns => _mounted.Count;

	/// <summary>Guns and cans being carried right now.</summary>
	public int CarriedItems => _carriedGun.Count + _carriedCan.Count;

	/// <summary>Cans spent on a belt this round. For the HUD and M8's CSV.</summary>
	public int CansSpent { get; private set; }

	/// <summary>Rounds put into belts this round.</summary>
	public int RoundsResupplied { get; private set; }

	public override void _Ready()
	{
		Instance = this;
		ProcessMode = ProcessModeEnum.Always;

		WeaponCatalog.Load();

		if (NetworkManager.Instance is { } net)
		{
			net.PeerJoined += OnPeerJoined;
		}
	}

	public override void _ExitTree()
	{
		if (Instance == this)
		{
			Instance = null;
		}
	}

	/// <summary>
	/// Where every gun and can is drawn. Presentation, on the render frame: nothing
	/// about their positions is replicated per tick, because each is either standing
	/// where it was put down or riding on a character whose position is already on
	/// the wire.
	/// </summary>
	public override void _Process(double delta)
	{
		if (Bootstrap.IsDedicatedServer || !_started)
		{
			return;
		}

		CombatManager combat = CombatManager.Instance;
		int local = NetworkManager.Instance?.LocalPeerId ?? 0;

		for (int i = 0; i < _guns.Count; i++)
		{
			Emplacement gun = _guns[i];
			fps_controller carrier = CharacterOf(combat, gun.CarrierPeerId);
			fps_controller gunner = CharacterOf(combat, gun.GunnerPeerId);
			gun.Place(carrier, gunner, gunner != null && gun.GunnerPeerId == local);
		}

		for (int i = 0; i < _cans.Count; i++)
		{
			AmmoCan can = _cans[i];
			can.Place(CharacterOf(combat, can.CarrierPeerId));
		}
	}

	// ---- tick, driven by PlayerManager -------------------------------------

	/// <summary>
	/// One server tick: bring spent cans back, take whatever the dead were holding off
	/// them, and let every mortar launcher with an empty tube take a can off the ground
	/// beside it.
	/// </summary>
	public void ServerTick(uint tick)
	{
		EnsureStarted();

		for (int i = 0; i < _cans.Count; i++)
		{
			AmmoCan can = _cans[i];
			if (can.Available || tick < can.RespawnTick)
			{
				continue;
			}

			can.ResetToHome();
			BroadcastCan(i);
		}

		ReleaseTheDead();
		FeedFromGround(tick);
	}

	/// <summary>A client has nothing to tick; it only needs the nodes found.</summary>
	public void ClientTick() => EnsureStarted();

	// ---- what a press means ------------------------------------------------

	/// <summary>
	/// Reads the use bit out of one player's input frame and does what it meant.
	/// Called by <see cref="CombatManager.ServerSimulate"/>, with the same frame the
	/// character was simulated from. Returns what the press meant, which for
	/// <see cref="UseAction.OpenLocker"/> is the caller's to act on: the panel is the
	/// player's own process's, and on a listen host that is this one.
	/// </summary>
	public UseAction ServerUse(PlayerCombat combat, in InputContext input, uint tick)
	{
		if (NetworkManager.Instance is { IsClient: true } || combat?.Character == null)
		{
			return UseAction.None;
		}

		UseAction action = Resolve(combat, combat.Use.Sample(input), out int gunIndex, out int canIndex,
			out int vehicleSlot);
		Act(combat, action, gunIndex, canIndex, vehicleSlot, tick);
		return action;
	}

	/// <summary>
	/// The owning client's reading of its own use press (docs/LOADOUT.md §7.2): the same
	/// tracker over the same predicted frames, resolved by the same rule against the
	/// guns, cans and lockers this client knows — what <see cref="PromptFor"/> already
	/// computes. Nothing is done with it but what is the client's to do, which is to open
	/// the locker's panel; every other action is the server's, a round trip later.
	/// Client-side, from <see cref="CombatManager.ClientSimulateLocal"/>, once per
	/// predicted tick and never on a replay.
	/// </summary>
	public UseAction PredictUse(PlayerCombat combat, in InputContext input)
	{
		if (combat?.Character == null)
		{
			return UseAction.None;
		}

		EnsureStarted();
		return Resolve(combat, combat.Use.Sample(input), out _, out _, out _);
	}

	/// <summary>
	/// Whether a tap of the use key where this player stands would get them into the crew
	/// vehicle in <paramref name="vehicleSlot"/> (docs/GROUND_VEHICLES.md §10, GV8): the
	/// table's own answer, so a bot taps only for what it means — not for a gun, a can or
	/// the locker nearer than the hull. Server only; it changes nothing.
	/// </summary>
	public bool TapWouldEnter(PlayerCombat combat, int vehicleSlot)
	{
		if (combat?.Character == null || vehicleSlot < 0)
		{
			return false;
		}

		EnsureStarted();
		return Resolve(combat, UseIntent.Tap, out _, out _, out int slot) == UseAction.EnterVehicle
			&& slot == vehicleSlot;
	}

	/// <summary>
	/// What one press means where this player is standing. The tracker is sampled by the
	/// caller whether or not they are alive, so it stays in step with the frames; a press
	/// from a corpse simply means nothing.
	/// </summary>
	private UseAction Resolve(PlayerCombat combat, UseIntent intent, out int gunIndex, out int canIndex,
		out int vehicleSlot)
	{
		gunIndex = -1;
		canIndex = -1;
		vehicleSlot = -1;
		if (intent == UseIntent.None || !combat.IsAlive)
		{
			return UseAction.None;
		}

		// Seated, the key is about the seat and nothing outside the vehicle is in reach
		// (docs/GROUND_VEHICLES.md §5.3): the table answers before it looks at anything else.
		CrewVehicleManager crew = CrewVehicleManager.Instance;
		bool seated = crew != null && crew.IsSeated(combat.PeerId);

		Vector3 at = combat.Character.SimPosition;
		gunIndex = NearestGun(at, combat.PeerId, out float gunDistance);
		canIndex = NearestCan(at, out float canDistance);
		float vehicleDistance = float.MaxValue;
		vehicleSlot = seated || crew == null ? -1 : crew.NearestEnterable(at, combat.PeerId, out vehicleDistance);
		float nearestItem = Mathf.Min(gunDistance, canDistance);
		float kioskDistance = crew?.KioskDistance(at) ?? float.PositiveInfinity;

		// The nearest of locker, kiosk, gun, can and hull wins (docs/GROUND_VEHICLES.md §5.3):
		// each of the locker and the kiosk is in reach only when nearer than everything else.
		var situation = new UseSituation(
			intent,
			CarryOf(combat.PeerId),
			_mounted.ContainsKey(combat.PeerId),
			gunIndex >= 0,
			canIndex >= 0 && canDistance <= gunDistance,
			combat.Character.IsOnFloor(),
			LockerInReach(at, Mathf.Min(Mathf.Min(nearestItem, vehicleDistance), kioskDistance)),
			seated,
			vehicleSlot >= 0 && vehicleDistance < nearestItem,
			KioskInReach(at, kioskDistance, Mathf.Min(nearestItem, vehicleDistance)));

		return EmplacementSim.Resolve(situation);
	}

	/// <summary>Carries out a decision. Server-side; each branch is one reliable message.</summary>
	private void Act(PlayerCombat combat, UseAction action, int gunIndex, int canIndex, int vehicleSlot, uint tick)
	{
		switch (action)
		{
			case UseAction.Mount:
				Mount(combat.PeerId, gunIndex);
				break;

			case UseAction.Dismount:
				Dismount(combat.PeerId);
				break;

			case UseAction.PickUpGun:
				PickUpGun(combat.PeerId, _mounted.TryGetValue(combat.PeerId, out int mounted) ? mounted : gunIndex);
				break;

			case UseAction.Deploy:
				Deploy(combat.PeerId, combat.Character.SimPosition, combat.Character.Yaw);
				break;

			case UseAction.PickUpCan:
				PickUpCan(combat.PeerId, canIndex);
				break;

			case UseAction.DropCan:
				DropCan(combat.PeerId, combat.Character.SimPosition);
				break;

			case UseAction.Resupply:
				// A mortar launcher is fed from the ground, so a can brought to one is set
				// down beside it, inside its metre, rather than emptied into it by hand.
				if (GunAt(gunIndex) is { FedFromGround: true } launcher)
				{
					DropCan(combat.PeerId, EmplacementSim.StackPoint(launcher.DeployPosition, launcher.DeployYaw,
						combat.Character.SimPosition));
				}
				else
				{
					Resupply(combat.PeerId, gunIndex, tick);
				}
				break;

			case UseAction.OpenLocker:
				// Nothing for the server to do: the panel is the owning client's, opened
				// from its own prediction of this press (PredictUse, docs/LOADOUT.md §7.2) —
				// or on a listen host by the caller, for the host's own player — and the kit
				// chosen in it arrives as a request (CombatManager.TryApplyLockerKit). The
				// press is still resolved here, so that at a locker it is not a can or a gun.
				break;

			// A crew vehicle's seats (docs/GROUND_VEHICLES.md §6.3): not predicted, so the
			// server's word on them is the only one (GV-D4).
			case UseAction.EnterVehicle:
				CrewVehicleManager.Instance?.Enter(combat, vehicleSlot);
				break;

			case UseAction.ExitVehicle:
				CrewVehicleManager.Instance?.Exit(combat.PeerId, tick);
				break;

			case UseAction.ChangeSeat:
				CrewVehicleManager.Instance?.ChangeSeat(combat.PeerId);
				break;
		}
	}

	// ---- transitions -------------------------------------------------------

	private void Mount(int peerId, int gunIndex)
	{
		Emplacement gun = GunAt(gunIndex);
		if (gun == null || gun.State != EmplacementStateId.Deployed || gun.GunnerPeerId != 0)
		{
			return;
		}

		gun.State = EmplacementStateId.Mounted;
		gun.GunnerPeerId = peerId;
		_mounted[peerId] = gunIndex;
		BroadcastGun(gunIndex);
	}

	private void Dismount(int peerId)
	{
		if (!_mounted.Remove(peerId, out int index))
		{
			return;
		}

		Emplacement gun = GunAt(index);
		if (gun == null)
		{
			return;
		}

		gun.State = EmplacementStateId.Deployed;
		gun.GunnerPeerId = 0;
		BroadcastGun(index);
	}

	private void PickUpGun(int peerId, int gunIndex)
	{
		Emplacement gun = GunAt(gunIndex);
		if (gun == null || gun.State == EmplacementStateId.Carried || CarryOf(peerId) != CarryKind.None)
		{
			return;
		}

		// Whoever was behind it is no longer behind it, including the person taking it.
		if (gun.GunnerPeerId != 0)
		{
			_mounted.Remove(gun.GunnerPeerId);
			gun.GunnerPeerId = 0;
		}

		gun.State = EmplacementStateId.Carried;
		gun.CarrierPeerId = peerId;
		_carriedGun[peerId] = gunIndex;
		BroadcastGun(gunIndex);
	}

	private void Deploy(int peerId, Vector3 at, float yaw)
	{
		if (!_carriedGun.Remove(peerId, out int index))
		{
			return;
		}

		Emplacement gun = GunAt(index);
		if (gun == null)
		{
			return;
		}

		gun.State = EmplacementStateId.Deployed;
		gun.CarrierPeerId = 0;

		// Where the player is standing and which way they are looking: the gun's
		// traverse arc is measured from this, so putting it down is the decision that
		// says what it can cover.
		gun.DeployPosition = at;
		gun.DeployYaw = yaw;
		BroadcastGun(index);
	}

	private void PickUpCan(int peerId, int canIndex)
	{
		AmmoCan can = CanAt(canIndex);
		if (can == null || !can.Available || can.IsCarried || CarryOf(peerId) != CarryKind.None)
		{
			return;
		}

		can.CarrierPeerId = peerId;
		_carriedCan[peerId] = canIndex;
		BroadcastCan(canIndex);
	}

	private void DropCan(int peerId, Vector3 at)
	{
		if (!_carriedCan.Remove(peerId, out int index))
		{
			return;
		}

		AmmoCan can = CanAt(index);
		if (can == null)
		{
			return;
		}

		can.CarrierPeerId = 0;
		can.RestPosition = at;
		BroadcastCan(index);
	}

	/// <summary>
	/// Empties a can into a belt. The can is spent whether or not all of it fitted:
	/// a player who walks one out to a gun that is nearly full has wasted the walk,
	/// which is the decision the can exists to create.
	/// </summary>
	private void Resupply(int peerId, int gunIndex, uint tick)
	{
		Emplacement gun = GunAt(gunIndex);
		if (!_carriedCan.TryGetValue(peerId, out int canIndex) || gun == null)
		{
			return;
		}

		AmmoCan can = CanAt(canIndex);
		if (can == null || !EmplacementSim.NeedsAmmo(gun.Weapon.Ammo, gun.Stats.MagazineSize))
		{
			return;
		}

		short before = gun.Weapon.Ammo;
		gun.Weapon.Ammo = EmplacementSim.Resupply(before, gun.Stats.MagazineSize, gun.RoundsPerCan(can));
		RoundsResupplied += gun.Weapon.Ammo - before;

		_carriedCan.Remove(peerId);
		Spend(canIndex, tick);
		BroadcastGun(gunIndex, forceAmmo: true);
	}

	/// <summary>
	/// Every deployed mortar launcher whose tube has room for a whole can takes one off
	/// the ground within <see cref="SimConfig.MortarFeedReachMeters"/> of it
	/// (<see cref="EmplacementSim.FeedsFrom"/>, <see cref="EmplacementSim.TakesCan"/>), the
	/// lowest-numbered first, as many as fit. Nobody has to be behind it: a can set down
	/// beside a launcher is the launcher's. The belt count is forced on its gunner's
	/// client, as a resupply's is, because nobody predicts a can they did not carry.
	/// </summary>
	private void FeedFromGround(uint tick)
	{
		for (int g = 0; g < _guns.Count; g++)
		{
			Emplacement gun = _guns[g];
			if (!gun.FedFromGround || !gun.IsDeployed)
			{
				continue;
			}

			bool fed = false;
			for (int c = 0; c < _cans.Count; c++)
			{
				AmmoCan can = _cans[c];
				int rounds = gun.RoundsPerCan(can);
				if (!EmplacementSim.TakesCan(gun.Weapon.Ammo, gun.Stats.MagazineSize, rounds))
				{
					break;
				}

				if (!can.Available || can.IsCarried || !EmplacementSim.FeedsFrom(gun.DeployPosition, can.RestPosition))
				{
					continue;
				}

				gun.Weapon.Ammo += (short)rounds;
				RoundsResupplied += rounds;
				Spend(c, tick);
				fed = true;
			}

			if (fed)
			{
				BroadcastGun(g, forceAmmo: true);
			}
		}
	}

	/// <summary>A can used up: off whoever had it, gone, and back at its home after <see cref="SimConfig.AmmoCanRespawnTicks"/>.</summary>
	private void Spend(int canIndex, uint tick)
	{
		AmmoCan can = CanAt(canIndex);
		if (can == null)
		{
			return;
		}

		CansSpent++;
		can.CarrierPeerId = 0;
		can.Available = false;
		can.RespawnTick = tick + SimConfig.AmmoCanRespawnTicks;
		can.RestPosition = can.HomePosition;
		BroadcastCan(canIndex);
	}

	/// <summary>Whether a can resting at <paramref name="can"/> is within a deployed launcher's metre.</summary>
	private bool FeedsALauncher(Vector3 can)
	{
		for (int i = 0; i < _guns.Count; i++)
		{
			Emplacement gun = _guns[i];
			if (gun.FedFromGround && gun.IsDeployed && EmplacementSim.FeedsFrom(gun.DeployPosition, can))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Cans on the ground that feed this launcher, for its gunner's prompt.</summary>
	private int CansBeside(Emplacement gun)
	{
		int count = 0;
		for (int i = 0; i < _cans.Count; i++)
		{
			AmmoCan can = _cans[i];
			if (can.Available && !can.IsCarried && EmplacementSim.FeedsFrom(gun.DeployPosition, can.RestPosition))
			{
				count++;
			}
		}

		return count;
	}

	/// <summary>
	/// Takes whatever the dead and the departed were holding off them. A gun is put
	/// down where its carrier fell, which is a gun somebody else can walk to; a can
	/// goes back to the spawn, because a can lying in a field is a can nobody will
	/// ever find.
	/// </summary>
	private void ReleaseTheDead()
	{
		CombatManager combat = CombatManager.Instance;

		for (int i = 0; i < _guns.Count; i++)
		{
			Emplacement gun = _guns[i];

			if (gun.GunnerPeerId != 0 && !IsUsable(combat, gun.GunnerPeerId))
			{
				_mounted.Remove(gun.GunnerPeerId);
				gun.GunnerPeerId = 0;
				gun.State = EmplacementStateId.Deployed;
				BroadcastGun(i);
			}

			if (gun.CarrierPeerId != 0 && !IsUsable(combat, gun.CarrierPeerId))
			{
				fps_controller character = CharacterOf(combat, gun.CarrierPeerId);
				_carriedGun.Remove(gun.CarrierPeerId);
				gun.CarrierPeerId = 0;
				gun.State = EmplacementStateId.Deployed;
				gun.DeployPosition = character?.SimPosition ?? gun.DeployPosition;
				BroadcastGun(i);
			}
		}

		for (int i = 0; i < _cans.Count; i++)
		{
			AmmoCan can = _cans[i];
			if (can.CarrierPeerId == 0 || IsUsable(combat, can.CarrierPeerId))
			{
				continue;
			}

			_carriedCan.Remove(can.CarrierPeerId);
			can.CarrierPeerId = 0;
			can.RestPosition = can.HomePosition;
			BroadcastCan(i);
		}
	}

	// ---- what the rest of the game asks ------------------------------------

	/// <summary>What this player is carrying, which is how fast they are walking.</summary>
	public CarryKind CarryOf(int peerId)
	{
		if (_carriedGun.ContainsKey(peerId))
		{
			return CarryKind.Gun;
		}

		return _carriedCan.ContainsKey(peerId) ? CarryKind.AmmoCan : CarryKind.None;
	}

	/// <summary>
	/// How much of their authored speed this player keeps. A mounted gunner is
	/// pinned rather than teleported onto a seat: they mounted from within arm's
	/// reach and they are not going anywhere until they let go, so there is nothing
	/// to teleport and nothing for a client to mispredict but a speed.
	/// </summary>
	public float MoveScaleOf(int peerId) =>
		_mounted.ContainsKey(peerId) ? 0f : EmplacementSim.MoveScale(CarryOf(peerId));

	/// <summary>The gun this player is behind, if any.</summary>
	public bool TryMounted(int peerId, out Emplacement gun)
	{
		gun = _mounted.TryGetValue(peerId, out int index) ? GunAt(index) : null;
		return gun != null;
	}

	/// <summary>The gun or launcher this player is carrying, if any.</summary>
	public Emplacement CarriedGun(int peerId) => _carriedGun.TryGetValue(peerId, out int index) ? GunAt(index) : null;

	public Emplacement GunAt(int index) => index >= 0 && index < _guns.Count ? _guns[index] : null;

	public AmmoCan CanAt(int index) => index >= 0 && index < _cans.Count ? _cans[index] : null;

	/// <summary>
	/// What the local player could do from where they are standing, for the HUD
	/// prompt. Presentation, and deliberately not the authority's answer: it is
	/// computed from the same reach against the same nodes, and being wrong about it
	/// costs a prompt.
	/// </summary>
	public string PromptFor(PlayerCombat combat, uint tick)
	{
		if (combat?.Character == null || !combat.IsAlive || !_started)
		{
			return string.Empty;
		}

		CrewVehicleManager crew = CrewVehicleManager.Instance;
		if (crew != null && crew.IsSeated(combat.PeerId))
		{
			return crew.SeatedPrompt(combat.PeerId, tick);
		}

		if (TryMounted(combat.PeerId, out Emplacement mounted))
		{
			if (mounted is MortarLauncher mortar)
			{
				int cans = CansBeside(mortar);
				float range = MortarLay.RangeOf(mortar.DeployPosition,
					mortar.TargetFor(combat.Character.EyePosition, combat.Character.Yaw, combat.Character.Pitch));
				return $"{mortar.Weapon.Ammo}/{mortar.Stats.MagazineSize} rounds"
					+ $"  +{cans * SimConfig.MortarRoundsPerCan} in {cans} can{(cans == 1 ? string.Empty : "s")}"
					+ $"  range {range:0} m   [E] dismount   [hold E] carry";
			}

			return $"{mounted.Weapon.Ammo}/{mounted.Stats.MagazineSize} belt"
				+ "   [E] dismount   [hold E] carry";
		}

		switch (CarryOf(combat.PeerId))
		{
			case CarryKind.Gun:
				return $"carrying the {CarriedGun(combat.PeerId)?.Noun ?? "heavy gun"}   [E] deploy";

			case CarryKind.AmmoCan:
				int near = NearestGun(combat.Character.SimPosition, combat.PeerId, out _);
				return near < 0 ? "carrying a can   [E] put it down"
					: GunAt(near).FedFromGround ? "carrying a can   [E] set it down by the mortar"
					: "carrying a can   [E] load the gun";
		}

		Vector3 at = combat.Character.SimPosition;
		int gun = NearestGun(at, combat.PeerId, out float gunDistance);
		int can = NearestCan(at, out float canDistance);
		float vehicleDistance = float.MaxValue;
		int vehicle = crew?.NearestEnterable(at, combat.PeerId, out vehicleDistance) ?? -1;
		float nearestItem = Mathf.Min(gunDistance, canDistance);
		float kioskDistance = crew?.KioskDistance(at) ?? float.PositiveInfinity;

		if (LockerInReach(at, Mathf.Min(Mathf.Min(nearestItem, vehicleDistance), kioskDistance)))
		{
			return "[E] weapon locker";
		}

		if (KioskInReach(at, kioskDistance, Mathf.Min(nearestItem, vehicleDistance)))
		{
			return "[E] vehicle kiosk";
		}

		if (vehicle >= 0 && vehicleDistance < nearestItem)
		{
			return crew.EnterPrompt(vehicle, at);
		}

		if (can >= 0 && (gun < 0 || canDistance <= gunDistance))
		{
			return "[E] take the ammunition can";
		}

		return gun < 0 ? string.Empty
			: GunAt(gun).FedFromGround ? "[E] mount   [hold E] carry the mortar"
			: "[E] mount   [hold E] carry the gun";
	}

	// ---- registry ----------------------------------------------------------

	/// <summary>
	/// Finds the map's guns and cans, once. Autoloads are ready before the map is,
	/// the same arrangement the barracks and the spawn points use. Both are sorted
	/// by name and capped: the index is what a state message names, so every peer
	/// has to derive the same one from the same scene.
	/// </summary>
	private void EnsureStarted()
	{
		// Not until the map is up. An autoload is ready before the main scene is, and
		// a scan that ran first would decide this map has no guns on it and never ask
		// again.
		if (_started || GetTree()?.CurrentScene == null)
		{
			return;
		}

		_started = true;

		foreach (Node node in GetTree().GetNodesInGroup(Emplacement.Group))
		{
			if (node is Emplacement gun)
			{
				_guns.Add(gun);
			}
		}

		foreach (Node node in GetTree().GetNodesInGroup(AmmoCan.Group))
		{
			if (node is AmmoCan can)
			{
				_cans.Add(can);
			}
		}

		// A locker is never named on the wire, so it needs neither the sort nor the
		// cap; sorted anyway, so a tie between two is broken the same way on every
		// peer and the prompt agrees with the server. Its reach is measured from its
		// footprint, so one a map has scaled or tilted is refused rather than measured
		// wrong (docs/LOADOUT.md §12).
		foreach (Node node in GetTree().GetNodesInGroup(WeaponLocker.Group))
		{
			if (node is not WeaponLocker locker)
			{
				continue;
			}

			if (!locker.IsUpright)
			{
				GD.PushError($"[emplacements] weapon locker {locker.Name} is scaled or tilted;"
					+ " its reach is measured from an upright 1.2 x 0.5 m footprint, so it is left out");
				continue;
			}

			_lockers.Add(locker);
		}

		_guns.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
		_cans.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
		_lockers.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

		Trim(_guns, SimConfig.MaxEmplacements, "heavy guns");
		Trim(_cans, SimConfig.MaxAmmoCans, "ammunition cans");
	}

	private static void Trim<T>(List<T> nodes, int cap, string what)
	{
		if (nodes.Count <= cap)
		{
			return;
		}

		GD.PushWarning($"[emplacements] {nodes.Count} {what}; only the first {cap} are addressable on the wire");
		nodes.RemoveRange(cap, nodes.Count - cap);
	}

	/// <summary>Everything back where the map put it, for the next round.</summary>
	public void ResetAll()
	{
		_carriedGun.Clear();
		_carriedCan.Clear();
		_mounted.Clear();
		CansSpent = 0;
		RoundsResupplied = 0;

		for (int i = 0; i < _guns.Count; i++)
		{
			_guns[i].ResetToHome();
			BroadcastGun(i);
		}

		for (int i = 0; i < _cans.Count; i++)
		{
			_cans[i].ResetToHome();
			BroadcastCan(i);
		}
	}

	/// <summary>
	/// The nearest deployed gun within reach, or -1. A gun somebody else is behind
	/// is not one to walk up to and take over.
	/// </summary>
	private int NearestGun(Vector3 at, int peerId, out float distance)
	{
		int best = -1;
		distance = float.MaxValue;

		for (int i = 0; i < _guns.Count; i++)
		{
			Emplacement gun = _guns[i];
			if (!gun.IsDeployed || (gun.GunnerPeerId != 0 && gun.GunnerPeerId != peerId))
			{
				continue;
			}

			float d = at.DistanceTo(gun.DeployPosition);
			if (d <= SimConfig.EmplacementReachMeters && d < distance)
			{
				distance = d;
				best = i;
			}
		}

		return best;
	}

	/// <summary>
	/// The nearest can within reach that is free to pick up, or -1. A can within a
	/// deployed mortar launcher's metre is the launcher's (<see cref="FeedsALauncher"/>):
	/// it is not offered to the use key, so the gunner walking back to the tube mounts it
	/// rather than picking up the can they have just set down beside it.
	/// </summary>
	private int NearestCan(Vector3 at, out float distance)
	{
		int best = -1;
		distance = float.MaxValue;

		for (int i = 0; i < _cans.Count; i++)
		{
			AmmoCan can = _cans[i];
			if (!can.Available || can.IsCarried || FeedsALauncher(can.RestPosition))
			{
				continue;
			}

			float d = at.DistanceTo(can.RestPosition);
			if (d <= SimConfig.EmplacementReachMeters && d < distance)
			{
				distance = d;
				best = i;
			}
		}

		return best;
	}

	/// <summary>
	/// Whether a weapon locker's footprint is within reach of <paramref name="at"/>
	/// (docs/LOADOUT.md L-D8) and nearer than <paramref name="nearestOther"/>, the
	/// nearest gun or can in reach — so a can set down beside a locker is still picked
	/// up by walking up to the can.
	/// </summary>
	private bool LockerInReach(Vector3 at, float nearestOther)
	{
		for (int i = 0; i < _lockers.Count; i++)
		{
			float d = LockerSupply.FootprintDistance(at, _lockers[i].GlobalPosition, _lockers[i].Yaw);
			if (LockerSupply.InReach(d) && d < nearestOther)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Whether the vehicle kiosk's footprint, <paramref name="kioskDistance"/> away, is in
	/// reach (docs/GROUND_VEHICLES.md §5.3: the locker's metre) and nearer than any locker
	/// and than <paramref name="nearestOther"/>, the nearest gun, can or hull in reach.
	/// </summary>
	private bool KioskInReach(Vector3 at, float kioskDistance, float nearestOther) =>
		KioskRules.InReach(kioskDistance) && kioskDistance < nearestOther && kioskDistance < LockerDistance(at);

	/// <summary>Whether this peer is still someone who could be holding something.</summary>
	private static bool IsUsable(CombatManager combat, int peerId)
	{
		PlayerCombat player = combat?.Find(peerId);
		return player != null && player.IsAlive;
	}

	private static fps_controller CharacterOf(CombatManager combat, int peerId) =>
		peerId == 0 ? null : combat?.Find(peerId)?.Character;

	// ---- replication -------------------------------------------------------

	/// <summary>
	/// <paramref name="forceAmmo"/> makes the belt count in the message
	/// authoritative even for the peer manning the gun. It is true exactly when
	/// something other than the gunner changed the belt — a can being loaded into it
	/// — because the gunner predicts its own firing and cannot predict somebody
	/// else's resupply.
	/// </summary>
	private void BroadcastGun(int index, bool forceAmmo = false) => SendGun(0, index, forceAmmo);

	private void BroadcastCan(int index) => SendCan(0, index);

	private void SendGun(int peerId, int index, bool forceAmmo = false)
	{
		Emplacement gun = GunAt(index);
		if (gun == null || !Multiplayer.HasMultiplayerPeer() || Multiplayer.GetPeers().Length == 0)
		{
			return;
		}

		if (peerId == 0)
		{
			Rpc(MethodName.ServerGunState, index, (byte)gun.State, gun.CarrierPeerId, gun.GunnerPeerId,
				gun.DeployPosition, gun.DeployYaw, (int)gun.Weapon.Ammo, forceAmmo);
			NetworkManager.Instance?.Stats.RecordSent(GunStateBytes * Multiplayer.GetPeers().Length);
			return;
		}

		RpcId(peerId, MethodName.ServerGunState, index, (byte)gun.State, gun.CarrierPeerId, gun.GunnerPeerId,
			gun.DeployPosition, gun.DeployYaw, (int)gun.Weapon.Ammo, forceAmmo);
		NetworkManager.Instance?.Stats.RecordSent(GunStateBytes);
	}

	private void SendCan(int peerId, int index)
	{
		AmmoCan can = CanAt(index);
		if (can == null || !Multiplayer.HasMultiplayerPeer() || Multiplayer.GetPeers().Length == 0)
		{
			return;
		}

		if (peerId == 0)
		{
			Rpc(MethodName.ServerCanState, index, can.CarrierPeerId, can.Available, can.RestPosition);
			NetworkManager.Instance?.Stats.RecordSent(CanStateBytes * Multiplayer.GetPeers().Length);
			return;
		}

		RpcId(peerId, MethodName.ServerCanState, index, can.CarrierPeerId, can.Available, can.RestPosition);
		NetworkManager.Instance?.Stats.RecordSent(CanStateBytes);
	}

	/// <summary>
	/// Server -> clients, reliable. One per transition, which is a handful a round:
	/// a gun's position is state and not a sample, and there is nothing in between
	/// two of these for a client to interpolate.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerGunState(int index, byte state, int carrier, int gunner, Vector3 position, float yaw,
		int ammo, bool forceAmmo)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsClient)
		{
			return;
		}

		net.Stats.RecordReceived(GunStateBytes);

		EnsureStarted();
		Emplacement gun = GunAt(index);
		if (gun == null || state > (byte)EmplacementStateId.Mounted)
		{
			return;
		}

		ApplyMembership(_carriedGun, gun.CarrierPeerId, carrier, index);
		ApplyMembership(_mounted, gun.GunnerPeerId, gunner, index);

		gun.State = (EmplacementStateId)state;
		gun.CarrierPeerId = carrier;
		gun.GunnerPeerId = gunner;
		gun.DeployPosition = position;
		gun.DeployYaw = yaw;

		// The local gunner predicts its own belt, exactly as it predicts its own
		// magazine, so the server's count — which is a round trip old — is only taken
		// while somebody else is behind the gun, or when somebody else changed it:
		// nobody can predict the can another player just carried over.
		if (forceAmmo || gunner == 0 || gunner != net.LocalPeerId)
		{
			gun.Weapon.Ammo = (short)Mathf.Clamp(ammo, 0, short.MaxValue);
		}
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerCanState(int index, int carrier, bool available, Vector3 position)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsClient)
		{
			return;
		}

		net.Stats.RecordReceived(CanStateBytes);

		EnsureStarted();
		AmmoCan can = CanAt(index);
		if (can == null)
		{
			return;
		}

		ApplyMembership(_carriedCan, can.CarrierPeerId, carrier, index);

		can.CarrierPeerId = carrier;
		can.Available = available;
		can.RestPosition = position;
	}

	/// <summary>
	/// Keeps a peer -> index table in step with a state message. A client holds the
	/// same tables the server does, because what it is carrying decides how fast it
	/// predicts itself walking.
	/// </summary>
	private static void ApplyMembership(Dictionary<int, int> table, int was, int now, int index)
	{
		if (was == now)
		{
			return;
		}

		if (was != 0)
		{
			table.Remove(was);
		}

		if (now != 0)
		{
			table[now] = index;
		}
	}

	/// <summary>A joining peer needs where everything is, not the next change to it.</summary>
	private void OnPeerJoined(int peerId)
	{
		if (NetworkManager.Instance is not { IsServer: true })
		{
			return;
		}

		EnsureStarted();

		for (int i = 0; i < _guns.Count; i++)
		{
			SendGun(peerId, i);
		}

		for (int i = 0; i < _cans.Count; i++)
		{
			SendCan(peerId, i);
		}
	}
}
