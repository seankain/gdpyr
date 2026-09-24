using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// Fifth autoload: the ground force's heavy guns and the cans that feed them
/// (docs/IMPLEMENTATION_PLAN.md §M5), and the weapon lockers at their spawn
/// (docs/NETCODE.md §10.6) — everything the use key does.
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

	/// <summary>Large weapons swapped at a locker this round. For the log and M8's CSV.</summary>
	public int LockerSwaps { get; private set; }

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

		for (int i = 0; i < _guns.Count; i++)
		{
			Emplacement gun = _guns[i];
			fps_controller carrier = CharacterOf(combat, gun.CarrierPeerId);
			fps_controller gunner = CharacterOf(combat, gun.GunnerPeerId);
			gun.Place(carrier, gunner?.Yaw ?? 0f, gunner != null);
		}

		for (int i = 0; i < _cans.Count; i++)
		{
			AmmoCan can = _cans[i];
			can.Place(CharacterOf(combat, can.CarrierPeerId));
		}
	}

	// ---- tick, driven by PlayerManager -------------------------------------

	/// <summary>
	/// One server tick: bring spent cans back, and take whatever the dead were
	/// holding off them.
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
	}

	/// <summary>A client has nothing to tick; it only needs the nodes found.</summary>
	public void ClientTick() => EnsureStarted();

	// ---- what a press means ------------------------------------------------

	/// <summary>
	/// Reads the use bit out of one player's input frame and does what it meant.
	/// Called by <see cref="CombatManager.ServerSimulate"/>, with the same frame the
	/// character was simulated from.
	/// </summary>
	public void ServerUse(PlayerCombat combat, in InputContext input, uint tick)
	{
		if (NetworkManager.Instance is { IsClient: true } || combat?.Character == null)
		{
			return;
		}

		UseIntent intent = combat.Use.Sample(input);
		if (intent == UseIntent.None)
		{
			return;
		}

		// Sampled above whether or not they are alive, so the tracker stays in step
		// with the frames; a press from a corpse simply does nothing.
		if (!combat.IsAlive)
		{
			return;
		}

		Vector3 at = combat.Character.SimPosition;
		int gunIndex = NearestGun(at, combat.PeerId, out float gunDistance);
		int canIndex = NearestCan(at, out float canDistance);

		var situation = new UseSituation(
			intent,
			CarryOf(combat.PeerId),
			_mounted.ContainsKey(combat.PeerId),
			gunIndex >= 0,
			canIndex >= 0 && canDistance <= gunDistance,
			combat.Character.IsOnFloor(),
			LockerInReach(at, Mathf.Min(gunDistance, canDistance)));

		Act(combat, EmplacementSim.Resolve(situation), gunIndex, canIndex, tick);
	}

	/// <summary>Carries out a decision. Server-side; each branch is one reliable message.</summary>
	private void Act(PlayerCombat combat, UseAction action, int gunIndex, int canIndex, uint tick)
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
				Resupply(combat.PeerId, gunIndex, tick);
				break;

			case UseAction.SwapWeapon:
				SwapWeapon(combat);
				break;
		}
	}

	/// <summary>
	/// Swaps the large weapon in this player's hands for the next one the locker
	/// holds (docs/NETCODE.md §10.6). For this life only: what a player spawns with
	/// is still the loadout menu's to say, which is up while they are dead. Carrying
	/// a locker's weapon into the next life would make a round's starting loadout
	/// depend on how the last one went — including for an agent seat, whose episodes
	/// are meant to start alike (docs/AGENT_API.md §7.1).
	///
	/// No message: the snapshot's weapon byte names the large weapon, so every
	/// client — the owner's prediction included — learns about it from the packet it
	/// was going to get anyway.
	/// </summary>
	private void SwapWeapon(PlayerCombat combat)
	{
		byte was = combat.Loadout.Large;
		combat.SwapLarge(WeaponCatalog.LockerSwap(was));

		LockerSwaps++;
		GD.Print($"[locker] peer {combat.PeerId} swapped the {WeaponCatalog.NameOf(was)}"
			+ $" for the {WeaponCatalog.NameOf(combat.Loadout.Large)}");
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
		gun.Weapon.Ammo = EmplacementSim.Resupply(before, gun.Stats.MagazineSize, can.Rounds);
		RoundsResupplied += gun.Weapon.Ammo - before;
		CansSpent++;

		_carriedCan.Remove(peerId);
		can.CarrierPeerId = 0;
		can.Available = false;
		can.RespawnTick = tick + SimConfig.AmmoCanRespawnTicks;
		can.RestPosition = can.HomePosition;

		BroadcastCan(canIndex);
		BroadcastGun(gunIndex, forceAmmo: true);
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

	public Emplacement GunAt(int index) => index >= 0 && index < _guns.Count ? _guns[index] : null;

	public AmmoCan CanAt(int index) => index >= 0 && index < _cans.Count ? _cans[index] : null;

	/// <summary>
	/// What the local player could do from where they are standing, for the HUD
	/// prompt. Presentation, and deliberately not the authority's answer: it is
	/// computed from the same reach against the same nodes, and being wrong about it
	/// costs a prompt.
	/// </summary>
	public string PromptFor(PlayerCombat combat)
	{
		if (combat?.Character == null || !combat.IsAlive || !_started)
		{
			return string.Empty;
		}

		if (TryMounted(combat.PeerId, out Emplacement mounted))
		{
			return $"{mounted.Weapon.Ammo}/{mounted.Stats.MagazineSize} belt"
				+ "   [E] dismount   [hold E] carry";
		}

		switch (CarryOf(combat.PeerId))
		{
			case CarryKind.Gun:
				return "carrying the heavy gun   [E] deploy";

			case CarryKind.AmmoCan:
				return NearestGun(combat.Character.SimPosition, combat.PeerId, out _) >= 0
					? "carrying a can   [E] load the gun"
					: "carrying a can   [E] put it down";
		}

		Vector3 at = combat.Character.SimPosition;
		int gun = NearestGun(at, combat.PeerId, out float gunDistance);
		int can = NearestCan(at, out float canDistance);

		if (LockerInReach(at, Mathf.Min(gunDistance, canDistance)))
		{
			byte large = combat.Loadout.Large;
			return $"[E] weapon locker: swap the {WeaponCatalog.NameOf(large)}"
				+ $" for the {WeaponCatalog.NameOf(WeaponCatalog.LockerSwap(large))}";
		}

		if (can >= 0 && (gun < 0 || canDistance <= gunDistance))
		{
			return "[E] take the ammunition can";
		}

		return gun >= 0 ? "[E] mount   [hold E] carry the gun" : string.Empty;
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
		// peer and the prompt agrees with the server.
		foreach (Node node in GetTree().GetNodesInGroup(WeaponLocker.Group))
		{
			if (node is WeaponLocker locker)
			{
				_lockers.Add(locker);
			}
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
		LockerSwaps = 0;

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

	private int NearestCan(Vector3 at, out float distance)
	{
		int best = -1;
		distance = float.MaxValue;

		for (int i = 0; i < _cans.Count; i++)
		{
			AmmoCan can = _cans[i];
			if (!can.Available || can.IsCarried)
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
	/// Whether a weapon locker is within reach of <paramref name="at"/> and nearer
	/// than <paramref name="nearestOther"/>, the nearest gun or can in reach — so a
	/// can set down beside a locker is still picked up by walking up to the can.
	/// </summary>
	private bool LockerInReach(Vector3 at, float nearestOther)
	{
		for (int i = 0; i < _lockers.Count; i++)
		{
			float d = at.DistanceTo(_lockers[i].GlobalPosition);
			if (d <= SimConfig.LockerReachMeters && d < nearestOther)
			{
				return true;
			}
		}

		return false;
	}

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
