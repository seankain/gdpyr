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
	/// What this life is carrying: the loadout it was spawned with, and since the
	/// weapon locker, whatever large weapon was swapped in at one since
	/// (docs/NETCODE.md §10.6). The next life starts from <see cref="PendingLoadout"/>
	/// again.
	/// </summary>
	public LoadoutSelection Loadout { get; private set; }

	/// <summary>The loadout the next spawn will use, as the player has asked for it.</summary>
	public LoadoutSelection PendingLoadout { get; set; }

	public readonly WeaponState[] Weapons = new WeaponState[SimConfig.WeaponSlots];

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
	/// The use key, as a tap or a hold (docs/IMPLEMENTATION_PLAN.md §M5).
	/// Server-side: emplacement transitions are the one player action here that is
	/// not predicted, so only the authority reads this
	/// (<see cref="EmplacementManager"/>).
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
	/// Client-side only: the last tick on which this client predicted a weapon
	/// event. The server's ammo count is a round trip old, so applying it to a
	/// weapon that has fired since would make the HUD count backwards.
	/// </summary>
	public uint LastPredictedWeaponTick { get; set; }

	public ref WeaponState Equipped => ref Weapons[Slot];

	public byte EquippedDefinitionId => Weapons[Slot].DefinitionId;

	public WeaponStats EquippedStats => WeaponCatalog.StatsFor(EquippedDefinitionId);

	/// <summary>What raising the equipped weapon's sights does.</summary>
	public AimStats EquippedAim => WeaponCatalog.AimFor(EquippedDefinitionId);

	/// <summary>Arms all three slots from a loadout and selects the large weapon.</summary>
	public void Equip(LoadoutSelection loadout)
	{
		Loadout = WeaponCatalog.Sanitize(loadout);
		for (int slot = 0; slot < SimConfig.WeaponSlots; slot++)
		{
			byte id = Loadout[slot];
			Weapons[slot] = WeaponState.Ready(id, WeaponCatalog.StatsFor(id));
		}

		// Sights come down with the weapons they were on: a life starts at the hip.
		Aim = default;

		// Spawning with the large weapon up is what a player wants every time; the
		// alternative is a keypress at the start of every life.
		Slot = SimConfig.LargeSlot;
	}

	/// <summary>
	/// Puts a different large weapon in the large slot, with a full magazine — what a
	/// weapon locker does (docs/NETCODE.md §10.6). The other two slots, and which
	/// slot is in the player's hands, are left alone: a player who walked up with the
	/// pistol out is still holding it.
	///
	/// Server-side when it is a locker's doing; a client calls it too, when the
	/// snapshot says the large weapon is not the one it has been predicting with.
	/// </summary>
	public void SwapLarge(byte largeWeaponId)
	{
		LoadoutSelection loadout = Loadout;
		loadout.Large = largeWeaponId;
		Loadout = WeaponCatalog.Sanitize(loadout);

		byte id = Loadout.Large;
		Weapons[SimConfig.LargeSlot] = WeaponState.Ready(id, WeaponCatalog.StatsFor(id));

		if (Slot == SimConfig.LargeSlot)
		{
			// The sights come down with the weapon they were on, as at a respawn.
			Aim = default;
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
		Equip(PendingLoadout);
	}

	/// <summary>Client-side: takes the server's word for health, ammunition, side and large weapon.</summary>
	public void ApplyAuthoritative(byte health, byte ammo, byte flags, uint ackTick, bool isLocal)
	{
		Health = health;
		Team = WeaponFlags.TeamOf(flags);

		// Which large weapon is the server's to say, and it is said in every snapshot,
		// so a swap at a locker — or a respawn with a different choice — reaches the
		// owner's prediction however many packets were lost on the way. Applied before
		// the ammunition below, so the count lands in the weapon it belongs to.
		if (WeaponCatalog.TryLargeWeaponAt(WeaponFlags.LargeChoice(flags), out byte large)
			&& Weapons[SimConfig.LargeSlot].DefinitionId != large)
		{
			SwapLarge(large);
		}

		int slot = WeaponFlags.Slot(flags);
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
		// would show the round it just spent coming back.
		if (ackTick >= LastPredictedWeaponTick)
		{
			Slot = slot;
			Weapons[slot].Ammo = ammo;
		}
	}

	public byte SnapshotFlags => WeaponFlags.Pack(Slot, Weapons[Slot].IsReloading, Team,
		WeaponCatalog.LargeChoiceOf(Weapons[SimConfig.LargeSlot].DefinitionId));

	public byte SnapshotAmmo => (byte)Mathf.Clamp(Weapons[Slot].Ammo, 0, byte.MaxValue);

	public byte SnapshotHealth => (byte)Mathf.Clamp(Health, 0, byte.MaxValue);
}
