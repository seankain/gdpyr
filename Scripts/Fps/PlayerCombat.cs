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

	public bool IsAlive => Health > 0;

	/// <summary>The tick a dead player comes back on. Only meaningful while dead.</summary>
	public uint RespawnTick { get; set; }

	/// <summary>Equipped slot: 0 melee, 1 sidearm, 2 large.</summary>
	public int Slot { get; set; }

	/// <summary>The loadout this life was spawned with.</summary>
	public LoadoutSelection Loadout { get; private set; }

	/// <summary>The loadout the next spawn will use, as the player has asked for it.</summary>
	public LoadoutSelection PendingLoadout { get; set; }

	public readonly WeaponState[] Weapons = new WeaponState[SimConfig.WeaponSlots];

	/// <summary>Where this player's hitbox has been (docs/NETCODE.md §5). Server-side.</summary>
	public readonly HitboxHistory History = new();

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

	/// <summary>Arms all three slots from a loadout and selects the large weapon.</summary>
	public void Equip(LoadoutSelection loadout)
	{
		Loadout = WeaponCatalog.Sanitize(loadout);
		for (int slot = 0; slot < SimConfig.WeaponSlots; slot++)
		{
			byte id = Loadout[slot];
			Weapons[slot] = WeaponState.Ready(id, WeaponCatalog.StatsFor(id));
		}

		// Spawning with the large weapon up is what a player wants every time; the
		// alternative is a keypress at the start of every life.
		Slot = SimConfig.WeaponSlots - 1;
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

	/// <summary>Brings the player back with a full magazine and whatever they chose next.</summary>
	public void Respawn()
	{
		Health = SimConfig.MaxHealth;
		Equip(PendingLoadout);
	}

	/// <summary>Client-side: takes the server's word for health and ammunition.</summary>
	public void ApplyAuthoritative(byte health, byte ammo, byte flags, uint ackTick, bool isLocal)
	{
		Health = health;

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

	public byte SnapshotFlags => WeaponFlags.Pack(Slot, Weapons[Slot].IsReloading);

	public byte SnapshotAmmo => (byte)Mathf.Clamp(Weapons[Slot].Ammo, 0, byte.MaxValue);

	public byte SnapshotHealth => (byte)Mathf.Clamp(Health, 0, byte.MaxValue);
}
