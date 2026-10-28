using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// One player's combat state: health, three weapons, and the record of where their
/// hitbox has been.
///
/// A plain class rather than a node. It outlives a death — the character node is
/// moved on respawn, not rebuilt — and putting it on the character would tie the
/// server's authority over a player's health to a scene it does not otherwise care
/// about.
/// </summary>
public sealed class PlayerCombat
{
	public PlayerCombat(int peerId, fps_controller character)
	{
		PeerId = peerId;
		Character = character;
		Loadout = WeaponCatalog.DefaultLoadout;
		PendingLoadout = Loadout;
		Equip(Loadout);
	}

	public int PeerId { get; }

	public fps_controller Character { get; set; }

	public int Health { get; private set; } = SimConfig.MaxHealth;

	/// <summary>
	/// Which side this player is on (docs/IMPLEMENTATION_PLAN.md §M3).
	///
	/// The server's copy is <see cref="Match.TeamService"/>'s and is mirrored here;
	/// a client's is driven by the team bit in the player snapshot, which is why it
	/// is a plain setter rather than something only the server may write
	/// (docs/NETCODE.md §7).
	/// </summary>
	public Team Team { get; set; } = Team.GroundForce;

	/// <summary>
	/// True while this player has been asked which side they want and has not
	/// answered (<see cref="Match.TeamService.RequireChoice"/>). The server's copy is
	/// the authority and is mirrored here the way <see cref="Team"/> is; a client
	/// sets its own from the request the server sends it.
	///
	/// Nothing new happens to a character while this is true — the player is simply
	/// not alive, which the simulation has meant "keep the look angles and ignore the
	/// rest" since M1. That is the whole reason the role menu needed no third team
	/// and no wire change (docs/IMPLEMENTATION_PLAN.md §M3).
	/// </summary>
	public bool AwaitingRole { get; set; }

	/// <summary>
	/// A strategist has no body on the field. Rather than a second lifecycle for a
	/// character that is present but not playing, they are simply never alive: the
	/// simulation already feeds a character that is not alive nothing but its look
	/// angles, hides it, takes it off the player collision layer and refuses to
	/// damage it. Switching sides is then one assignment in each direction.
	///
	/// A player who has not yet picked a side is not alive either, for the same
	/// reason and by the same mechanism.
	/// </summary>
	public bool IsAlive => !AwaitingRole && Team == Team.GroundForce && Health > 0;

	/// <summary>True for a player who is on the ground and has been killed.</summary>
	public bool IsDowned => !AwaitingRole && Team == Team.GroundForce && Health <= 0;

	/// <summary>The tick a dead player comes back on. Only meaningful while dead.</summary>
	public uint RespawnTick { get; set; }

	/// <summary>Equipped slot: 0 melee, 1 sidearm, 2 large.</summary>
	public int Slot { get; set; }

	/// <summary>
	/// What this life is carrying: the kit it was spawned with, or the one it took at
	/// a weapon locker since (docs/LOADOUT.md §5.3). The next life starts from
	/// <see cref="PendingLoadout"/>, which a locker's kit also sets (L-D11). On a
	/// client it is the server's word, read off the player record (docs/LOADOUT.md
	/// §6.1), which is what its reserve and its speed are derived from.
	/// </summary>
	public LoadoutSelection Loadout
	{
		get => _loadout;
		private set
		{
			_loadout = value;
			LoadKg = WeaponCatalog.WeightOf(value);
			LoadScale = Sim.Loadout.SpeedScale(LoadKg, WeaponCatalog.ReferenceWeightKg);
		}
	}

	private LoadoutSelection _loadout;

	/// <summary>
	/// What <see cref="Loadout"/> weighs [kg]: the kit as taken, not as spent (L-D5), so
	/// it changes where the kit does — a spawn, a locker, and on a client the record —
	/// and nowhere else.
	/// </summary>
	public float LoadKg { get; private set; }

	/// <summary>
	/// The share of their movement states' speeds this kit lets the player keep (L-D4),
	/// 1 for the default kit; multiplied by the carry's scale before every simulated
	/// tick, on the server and the owning client alike (docs/LOADOUT.md §5.5).
	/// </summary>
	public float LoadScale { get; private set; } = 1f;

	/// <summary>The loadout the next spawn will use, as the player has asked for it.</summary>
	public LoadoutSelection PendingLoadout { get; set; }

	public readonly WeaponState[] Weapons = new WeaponState[SimConfig.WeaponSlots];

	/// <summary>
	/// Hand grenades this life (docs/NETCODE.md §10.9): the kit's grenade pouches at
	/// every spawn (docs/LOADOUT.md L-D1), thrown by their own key whatever slot is up.
	/// Stepped from the recorded frames on the server and the owning client alike, as
	/// a weapon is.
	/// </summary>
	public GrenadeState Grenades;

	/// <summary>
	/// The vest this life is wearing and the points it has left (docs/LOADOUT.md §4.3):
	/// full at every spawn and when a locker gives a different one, worn down by the
	/// hits it takes (<see cref="BodyArmour.Absorb"/>). Server-side it is the authority;
	/// a client's is the record's, which carries the points whole (§6.1) — nothing on a
	/// client predicts damage.
	/// </summary>
	public BodyArmourState Vest;

	/// <summary>
	/// How far up this player's sights are (<see cref="Ads"/>). Advanced by the same
	/// pure step on the server and on the owning client, from the same recorded
	/// frames, so it needs no room in the snapshot: nobody but the player behind them
	/// can see their own sights, and the one process that draws them is the one that
	/// sampled the button.
	/// </summary>
	public AimState Aim;

	/// <summary>Where this player's hitbox has been (docs/NETCODE.md §5). Server-side.</summary>
	public readonly HitboxHistory History = new();

	/// <summary>
	/// The use key, as a tap or a hold (docs/IMPLEMENTATION_PLAN.md §M5). On the
	/// authority it decides emplacement transitions, the one player action here that is
	/// not predicted (<see cref="EmplacementManager.ServerUse"/>). On the owning client
	/// it is stepped over the same predicted frames for one thing only: a tap at a
	/// weapon locker opens its panel there (<see cref="EmplacementManager.PredictUse"/>,
	/// docs/LOADOUT.md §7.2). A process steps one player's tracker one way or the other,
	/// never both.
	/// </summary>
	public UseTracker Use;

	/// <summary>
	/// How far back this player's shots are compensated, in ticks. Derived from the
	/// RTT their client reports and clamped hard, because that number is a claim and
	/// not a measurement (docs/NETCODE.md §4.3).
	/// </summary>
	public int LagCompensationTicks { get; set; }

	public int Kills { get; set; }

	public int Deaths { get; set; }

	/// <summary>Shots this player's weapons have fired. For the net HUD.</summary>
	public int ShotsFired { get; set; }

	/// <summary>
	/// Server-side: rounds the large slot has fired this life, reported as a kill's
	/// <c>rounds_fired</c> (docs/LOADOUT.md L0, which sizes the default kit's spare
	/// magazines from it). Reset at every spawn and not by a locker's kit: the life
	/// spending them is the same one.
	/// </summary>
	public int PrimaryRoundsThisLife { get; set; }

	/// <summary>
	/// Server-side: the tick this player last took a kit at a locker, 0 for never — what
	/// holds them to one a quarter-second (<see cref="KitRequest.MinIntervalTicks"/>).
	/// </summary>
	public uint LastKitTick { get; set; }

	/// <summary>
	/// The tick anything last hurt this player, a hit the vest took whole included; 0
	/// for never this life. What the locker's resupply waits out (docs/LOADOUT.md L-D7,
	/// <see cref="Resupply.DamageLockoutTicks"/>). Never replicated: the server sets it
	/// in its damage; a client sets its own player's when the record shows health or
	/// the vest's points falling, a one-way trip late, for the HUD's resupply prompt
	/// and nothing else.
	/// </summary>
	public uint LastDamagedTick { get; set; }

	/// <summary>
	/// Server-side: when the locker's resupply may next give this player rounds, a
	/// grenade and armour points (<see cref="LockerSupply.Step"/>). For the life: a
	/// spawn starts it afresh, and leaving the reach does not.
	/// </summary>
	public SupplyPacing Supply;

	/// <summary>
	/// Server-side: a ground bot that plans, which perceives the strategist's fire
	/// (docs/TACTICS.md §5.7). Set by the bot director; false for a person, who is
	/// suppressed by rounds and nothing else, and for a legacy bot, which plans nothing.
	/// </summary>
	public bool PerceivesFire { get; set; }

	/// <summary>
	/// Server-side, for a player that <see cref="PerceivesFire"/>: the last tick a round of
	/// the strategist's side passed within <see cref="NearMiss.Meters"/> of it or hit it, or
	/// one's blast reached it; 0 for never this life. Never replicated.
	/// </summary>
	public uint LastFiredUponTick { get; set; }

	/// <summary>
	/// Client-side only: the last tick on which this client predicted a weapon
	/// event. The server's ammo count is a round trip old, so applying it to a
	/// weapon that has fired since would make the HUD count backwards.
	/// </summary>
	public uint LastPredictedWeaponTick { get; set; }

	/// <summary>
	/// Client-side only: the last tick this client predicted a throw on, for the reason
	/// <see cref="LastPredictedWeaponTick"/> exists — the snapshot's grenade count is a
	/// round trip old.
	/// </summary>
	public uint LastPredictedThrowTick { get; set; }

	public ref WeaponState Equipped => ref Weapons[Slot];

	public byte EquippedDefinitionId => Weapons[Slot].DefinitionId;

	public WeaponStats EquippedStats => WeaponCatalog.StatsFor(EquippedDefinitionId);

	/// <summary>What raising the equipped weapon's sights does.</summary>
	public AimStats EquippedAim => WeaponCatalog.AimFor(EquippedDefinitionId);

	/// <summary>
	/// A bit per slot that holds a weapon, for <see cref="WeaponSim.SelectSlot"/>: all
	/// three, or the hammer and the pistol with no primary. From the kit, which the
	/// server and the owning client both know.
	/// </summary>
	public int OccupiedSlots =>
		Weapons[SimConfig.LargeSlot].IsEmpty ? WeaponSim.AllSlots & ~(1 << SimConfig.LargeSlot) : WeaponSim.AllSlots;

	/// <summary>
	/// Arms every slot from a kit (docs/LOADOUT.md §5.1): the primary with a full
	/// magazine and its pouches' rounds spare, the grenades the kit's pouches hold, the
	/// vest full, and the primary up — or the pistol, when the kit has none.
	/// </summary>
	public void Equip(LoadoutSelection loadout)
	{
		Loadout = WeaponCatalog.Sanitize(loadout);
		for (int slot = 0; slot < SimConfig.LargeSlot; slot++)
		{
			byte id = Loadout[slot];
			Weapons[slot] = WeaponState.Ready(id, WeaponCatalog.StatsFor(id));
		}

		ArmLarge();

		// Sights come down with the weapons they were on: a life starts at the hip.
		Aim = default;

		// A life starts with its grenade pouches full, as it does with full magazines.
		Grenades = GrenadeState.Full(Loadout.Grenades);
		Vest = BodyArmourState.Full(Loadout.Armour);
		Supply = default;
		PrimaryRoundsThisLife = 0;

		// Spawning with the large weapon up is what a player wants every time; the
		// alternative is a keypress at the start of every life. With none, the pistol.
		Slot = Loadout.HasPrimary ? SimConfig.LargeSlot : SimConfig.SidearmSlot;
	}

	/// <summary>
	/// Takes a kit at a weapon locker (docs/LOADOUT.md L-D10): only what changed is
	/// re-equipped. A different primary comes with a full magazine and full pouches;
	/// the same one keeps its magazine and spare rounds, losing what fewer pouches no
	/// longer hold; grenades are kept up to the kit's grenade pouches; a different
	/// vest is full, and the same one keeps the points it has left. A pouch added
	/// is empty until the locker's resupply fills it (<see cref="TakeResupply"/>), so
	/// asking under fire refills nothing — and asking for the vest already worn refills
	/// nothing either. The hammer, the pistol, and which slot is in the player's hands
	/// are left alone — a player who walked up with the pistol out is still holding
	/// it — unless the primary in them is given up, when the pistol comes up.
	///
	/// Server-side: the validator's to call (<c>CombatManager.TryApplyLockerKit</c>).
	/// A client learns the result from the player record, as it learns a respawn's.
	/// </summary>
	public void ApplyKit(LoadoutSelection kit)
	{
		Loadout = WeaponCatalog.Sanitize(kit);
		byte id = Loadout.Large;
		bool changed = Weapons[SimConfig.LargeSlot].DefinitionId != id;
		Weapons[SimConfig.LargeSlot] = Sim.Loadout.RefitPrimary(Weapons[SimConfig.LargeSlot], Loadout,
			WeaponCatalog.StatsFor(id), WeaponCatalog.CarryFor(id));
		Grenades.Count = Sim.Loadout.RefitGrenades(Grenades.Count, Loadout);
		Vest = Sim.Loadout.RefitVest(Vest, Loadout);

		if (changed)
		{
			HandOverLarge();
		}
	}

	/// <summary>
	/// What the locker's resupply would top up, as this player carries it now: the
	/// primary's spare rounds (none with no primary), the grenades and the vest's points.
	/// </summary>
	public KitState Carried => new(
		Weapons[SimConfig.LargeSlot].IsEmpty ? 0 : Weapons[SimConfig.LargeSlot].Reserve,
		Grenades.Count,
		Vest.Points);

	/// <summary>
	/// One tick at a weapon locker (docs/LOADOUT.md L-D7): steps the resupply over what
	/// this player carries, up to their kit, and writes back what it gave. Server-side —
	/// the resupply pass's to call, for a living player in a locker's reach — and the
	/// record carries the result to every client: the reserve under the owner's
	/// acknowledgement guard, as a reload's, the grenades under the throw's, the vest whole.
	/// </summary>
	public SupplyGiven TakeResupply(uint tick)
	{
		KitState kit = Carried;
		SupplyGiven given = LockerSupply.Step(ref kit, ref Supply, Loadout, WeaponCatalog.CarryFor(Loadout.Large),
			tick, LastDamagedTick);

		if (given.Rounds > 0)
		{
			Weapons[SimConfig.LargeSlot].Reserve = (short)kit.Reserve;
		}

		if (given.Grenades > 0)
		{
			Grenades.Count = (byte)kit.Grenades;
		}

		if (given.ArmourPoints > 0)
		{
			Vest = new BodyArmourState(Vest.Tier, kit.ArmourPoints);
		}

		return given;
	}

	/// <summary>
	/// Fills the large slot from <see cref="Loadout"/>: its primary, full, or nothing.
	/// The sights come down with a weapon taken out of the hands, and a player left
	/// holding an empty slot is handed the pistol.
	/// </summary>
	private void ArmLarge()
	{
		byte id = Loadout.Large;
		Weapons[SimConfig.LargeSlot] = Loadout.HasPrimary
			? WeaponState.Ready(id, WeaponCatalog.StatsFor(id), Sim.Loadout.ReserveFor(Loadout, WeaponCatalog.CarryFor(id)))
			: WeaponState.Empty;
		HandOverLarge();
	}

	/// <summary>What changing the weapon in the large slot does to the hands holding it.</summary>
	private void HandOverLarge()
	{
		if (Slot == SimConfig.LargeSlot)
		{
			// The sights come down with the weapon they were on, as at a respawn.
			Aim = default;
			if (!Loadout.HasPrimary)
			{
				Slot = SimConfig.SidearmSlot;
			}
		}
	}

	/// <summary>Returns true when this damage was the killing blow.</summary>
	public bool ApplyDamage(float amount)
	{
		if (!IsAlive || amount <= 0f)
		{
			return false;
		}

		Health = Mathf.Max(0, Health - Mathf.RoundToInt(amount));
		return !IsAlive;
	}

	public void Kill()
	{
		Health = 0;
		Deaths++;
	}

	/// <summary>
	/// Takes this player off the field until they pick a side. Server-side, and the
	/// one thing the role menu does to the simulation.
	///
	/// The health goes to zero as well as the flag, because that is what the rest of
	/// the network already reads: a snapshot carries health and not intentions, so
	/// every other client hides the body through the path a death goes through. No
	/// ticket is charged — nobody killed them (docs/NETCODE.md §7).
	/// </summary>
	public void HoldForRoleChoice()
	{
		AwaitingRole = true;
		Health = 0;
	}

	/// <summary>Brings the player back with a full magazine and whatever they chose next.</summary>
	public void Respawn()
	{
		Health = SimConfig.MaxHealth;
		LastFiredUponTick = 0;
		LastDamagedTick = 0;
		Equip(PendingLoadout);
	}

	/// <summary>
	/// Client-side: takes the server's word for health, ammunition, side, the kit,
	/// grenades and the vest. <paramref name="reserve"/> is the primary's spare rounds,
	/// <paramref name="armourPoints"/> the vest's points and <paramref name="kit"/> the
	/// record's kit byte (docs/LOADOUT.md §6.1).
	/// </summary>
	public void ApplyAuthoritative(byte health, byte ammo, byte flags, ushort reserve, byte armourPoints, byte kit,
		uint ackTick, bool isLocal)
	{
		Health = health;
		Team = WeaponFlags.TeamOf(flags);

		// The kit is the server's to say, and it is said in every snapshot, so a kit
		// taken at a locker — or a respawn with a different choice — reaches the owner's
		// prediction however many packets were lost on the way — and with it the speed
		// the owner predicts its walk at (docs/LOADOUT.md §7.1). The large weapon is the
		// weapon byte's, zero for none; the pouches and the vest are the kit byte's.
		// Applied before the ammunition below, so the count lands in the weapon it
		// belongs to.
		LoadoutSelection recorded = WeaponCatalog.RecordedKit(flags, kit);

		bool swapped = Weapons[SimConfig.LargeSlot].DefinitionId != recorded.Large;
		Loadout = recorded;
		if (swapped)
		{
			ArmLarge();
		}

		// Whole, every snapshot, for everyone: only the server applies damage, so there
		// is no prediction for the vest's points to wait on, as health has none.
		Vest = new BodyArmourState(recorded.Armour, armourPoints);

		int slot = WeaponFlags.Slot(flags);

		// The owner predicts its own throws, so the server's count waits until it
		// accounts for every one of them, as the magazine's does below.
		if (!isLocal || ackTick >= LastPredictedThrowTick)
		{
			Grenades.Count = (byte)WeaponFlags.Grenades(flags);
		}

		if (!isLocal)
		{
			// A remote player's weapon is only ever the server's to say, and its
			// magazine is nobody else's business — the snapshot's ammo byte is for the
			// owner's HUD.
			Slot = slot;
			return;
		}

		// The owner predicts its own weapon, so the server's count is only applied
		// once it accounts for everything predicted locally. Otherwise every shot
		// would show the round it just spent coming back. The reserve is predicted
		// the same way, by the reloads the owner steps itself (docs/LOADOUT.md §7.1).
		if (ackTick >= LastPredictedWeaponTick)
		{
			Slot = slot;
			if (!Weapons[slot].IsEmpty)
			{
				Weapons[slot].Ammo = ammo;
			}

			if (!Weapons[SimConfig.LargeSlot].IsEmpty)
			{
				Weapons[SimConfig.LargeSlot].Reserve = (short)reserve;
			}
		}
	}

	public byte SnapshotFlags => WeaponFlags.Pack(Slot, Weapons[Slot].IsReloading, Team,
		WeaponCatalog.LargeChoiceOf(Weapons[SimConfig.LargeSlot].DefinitionId), Grenades.Count);

	public byte SnapshotAmmo => (byte)Mathf.Clamp(Weapons[Slot].Ammo, 0, byte.MaxValue);

	/// <summary>The primary's spare rounds for the record: none with no primary.</summary>
	public ushort SnapshotReserve => (ushort)Mathf.Clamp(Weapons[SimConfig.LargeSlot].Reserve, 0, KitFlags.MaxReserve);

	/// <summary>The vest's points for the record: none with no vest.</summary>
	public byte SnapshotArmour => (byte)Mathf.Clamp(Vest.Points, 0, KitFlags.MaxArmourPoints);

	/// <summary>The kit for the record (<see cref="KitFlags"/>).</summary>
	public byte SnapshotKit => KitFlags.Pack(Loadout);

	public byte SnapshotHealth => (byte)Mathf.Clamp(Health, 0, byte.MaxValue);
}
