using System;
using System.Text;
using Godot;

namespace Gdpyr.Sim;

/// <summary>What the loadout panel is editing a kit for (docs/LOADOUT.md §7.2, §7.3).</summary>
public enum DraftMode : byte
{
	/// <summary>At a weapon locker: taken now, through the validator, and the next spawn's too (L-D11).</summary>
	Locker = 0,

	/// <summary>From the death screen: the next spawn's kit alone, which the server takes at any time.</summary>
	NextSpawn = 1,
}

/// <summary>One thing a key or a click asks of the loadout panel.</summary>
public enum DraftAction : byte
{
	None = 0,

	/// <summary>The first primary in the panel's order: none.</summary>
	Primary1,
	Primary2,
	Primary3,
	Primary4,
	FewerMagazines,
	MoreMagazines,
	FewerGrenades,
	MoreGrenades,
	LighterVest,
	HeavierVest,

	/// <summary>Take the kit: ask the locker for it, or keep it for the next spawn.</summary>
	Take,

	/// <summary>Back to the kit the panel opened on: what the player carries, or the next spawn's as it was.</summary>
	Revert,

	/// <summary>The default kit (L-D3).</summary>
	Default,
	Close,
}

/// <summary>What the panel has to do after an action or an answer.</summary>
public enum DraftOutcome : byte
{
	/// <summary>Nothing changed: the action was disabled, or asked for what the kit already is.</summary>
	None = 0,

	/// <summary>The kit or the footer changed; draw it again.</summary>
	Edited,

	/// <summary>Send the kit to the locker's validator and wait for the answer, which <see cref="LoadoutDraft.Answer"/> takes.</summary>
	AskLocker,

	/// <summary>Send the kit as the next spawn's, and close: nothing comes back for it.</summary>
	SetNextSpawn,

	Close,
}

/// <summary>What the panel needs to know each tick to decide whether it is still wanted.</summary>
public readonly struct DraftSituation
{
	/// <summary>A living ground-force player (<c>PlayerCombat.IsAlive</c>).</summary>
	public readonly bool Alive;

	/// <summary>On the ground force, as opposed to in a strategist's chair.</summary>
	public readonly bool GroundForce;

	/// <summary>The role menu is up, which takes the middle of the screen and the mouse.</summary>
	public readonly bool AwaitingRole;

	public readonly bool RoundEnded;

	/// <summary>Flat distance from the player's feet to the nearest locker's footprint; infinite on a map without one.</summary>
	public readonly float LockerDistance;

	public DraftSituation(bool alive, bool groundForce, bool awaitingRole, bool roundEnded, float lockerDistance)
	{
		Alive = alive;
		GroundForce = groundForce;
		AwaitingRole = awaitingRole;
		RoundEnded = roundEnded;
		LockerDistance = lockerDistance;
	}
}

/// <summary>
/// The numbers the loadout panel reads its rows and read-outs off: the weapon
/// catalog's, handed in, because the catalog loads resources and this is tested
/// without them. Every process that draws a panel builds one from the same catalog
/// the simulation weighs and arms kits from, so the panel cannot disagree with it.
/// </summary>
public sealed class DraftCatalog
{
	/// <param name="primaries">The primaries in the panel's order, keys 1 to 4; the first is <see cref="Loadout.NoPrimary"/>.</param>
	/// <param name="largeWeapons">The catalog's large weapons, what a kit is validated against.</param>
	/// <param name="carry">Carry stats by weapon id.</param>
	/// <param name="magazineSizes">Magazine sizes by weapon id.</param>
	/// <param name="names">Names by weapon id.</param>
	/// <param name="defaultKit">The default kit (L-D3), with the hammer and the pistol every kit carries.</param>
	/// <param name="referenceKg">What the default kit weighs: the speed line's reference (L-D4).</param>
	/// <param name="walkSpeed">The walking state's authored speed [m/s].</param>
	/// <param name="sprintSpeed">The sprinting state's authored speed [m/s].</param>
	/// <param name="referenceRound">The round a vest's hits-to-kill is shown against: the rifle's (§7.2).</param>
	/// <param name="referenceDamage">That round's damage.</param>
	public DraftCatalog(byte[] primaries, byte[] largeWeapons, CarryStats[] carry, int[] magazineSizes,
		string[] names, LoadoutSelection defaultKit, float referenceKg, float walkSpeed, float sprintSpeed,
		string referenceRound, float referenceDamage)
	{
		Primaries = primaries ?? Array.Empty<byte>();
		LargeWeapons = largeWeapons ?? Array.Empty<byte>();
		Carry = carry ?? Array.Empty<CarryStats>();
		MagazineSizes = magazineSizes ?? Array.Empty<int>();
		Names = names ?? Array.Empty<string>();
		DefaultKit = defaultKit;
		ReferenceKg = referenceKg;
		WalkSpeed = walkSpeed;
		SprintSpeed = sprintSpeed;
		ReferenceRound = referenceRound ?? string.Empty;
		ReferenceDamage = referenceDamage;
	}

	public byte[] Primaries { get; }
	public byte[] LargeWeapons { get; }
	public CarryStats[] Carry { get; }
	public int[] MagazineSizes { get; }
	public string[] Names { get; }
	public LoadoutSelection DefaultKit { get; }
	public float ReferenceKg { get; }
	public float WalkSpeed { get; }
	public float SprintSpeed { get; }
	public string ReferenceRound { get; }
	public float ReferenceDamage { get; }

	public string NameOf(byte id) =>
		id == Loadout.NoPrimary ? "none" : id < Names.Length && Names[id] != null ? Names[id] : $"#{id}";

	public CarryStats CarryOf(byte id) => id < Carry.Length ? Carry[id] : default;

	public int MagazineOf(byte id) => id < MagazineSizes.Length ? MagazineSizes[id] : 0;
}

/// <summary>
/// The loadout panel's model (docs/LOADOUT.md §7.2, §7.3): the kit being edited, what
/// each key and click does to it, what is disabled, what the read-outs say and what
/// each answer from the locker does. The widget, <c>Scripts/Ui/LoadoutPanel.cs</c>,
/// draws this and nothing else.
///
/// A plain class under <c>Scripts/Sim</c> so that every row, limit and refusal is a
/// question <c>dotnet test</c> answers without a window. The kit it holds is always
/// one <see cref="Loadout.IsValid"/> accepts as it stands: a panel that could ask for a
/// kit the validator would refuse as <c>invalid</c> would be a panel that lies.
/// </summary>
public sealed class LoadoutDraft
{
	private readonly DraftCatalog _catalog;

	public LoadoutDraft(DraftCatalog catalog)
	{
		_catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
		Kit = Sane(catalog.DefaultKit);
		Original = Kit;
	}

	public DraftCatalog Catalog => _catalog;

	public bool IsOpen { get; private set; }

	public DraftMode Mode { get; private set; }

	/// <summary>The kit being edited.</summary>
	public LoadoutSelection Kit { get; private set; }

	/// <summary>The kit the panel opened on, which <see cref="DraftAction.Revert"/> goes back to.</summary>
	public LoadoutSelection Original { get; private set; }

	/// <summary>A kit has been sent to the locker and its answer has not come back; taking another waits for it.</summary>
	public bool Awaiting { get; private set; }

	/// <summary>The refusal the locker gave the last kit asked for; <see cref="KitRefusal.None"/> once anything else happens.</summary>
	public KitRefusal LastRefusal { get; private set; }

	/// <summary>
	/// Opens on <paramref name="kit"/>: at a locker, what the player carries; from the
	/// death screen, the next spawn's. A kit the rules would trim is trimmed, so the
	/// panel never starts from one it could not ask for.
	/// </summary>
	public void Open(DraftMode mode, LoadoutSelection kit)
	{
		IsOpen = true;
		Mode = mode;
		Kit = Sane(kit);
		Original = Kit;
		Awaiting = false;
		LastRefusal = KitRefusal.None;
	}

	public void Close()
	{
		IsOpen = false;
		Awaiting = false;
	}

	/// <summary>
	/// The panel's keys (L-D12): <c>1</c>–<c>4</c> the primary, <c>5</c>/<c>6</c> fewer
	/// and more magazines, <c>7</c>/<c>8</c> grenades, <c>9</c>/<c>0</c> a lighter and a
	/// heavier vest, Enter takes the kit, Backspace goes back to the one the panel opened
	/// on, <c>D</c> is the default kit, and <c>E</c> or Escape closes. The widget also
	/// closes on the <c>use</c> action, whatever key it is bound to.
	/// </summary>
	public static DraftAction ActionOf(Key key) => key switch
	{
		Key.Key1 => DraftAction.Primary1,
		Key.Key2 => DraftAction.Primary2,
		Key.Key3 => DraftAction.Primary3,
		Key.Key4 => DraftAction.Primary4,
		Key.Key5 => DraftAction.FewerMagazines,
		Key.Key6 => DraftAction.MoreMagazines,
		Key.Key7 => DraftAction.FewerGrenades,
		Key.Key8 => DraftAction.MoreGrenades,
		Key.Key9 => DraftAction.LighterVest,
		Key.Key0 => DraftAction.HeavierVest,
		Key.Enter or Key.KpEnter => DraftAction.Take,
		Key.Backspace => DraftAction.Revert,
		Key.D => DraftAction.Default,
		Key.E or Key.Escape => DraftAction.Close,
		_ => DraftAction.None,
	};

	/// <summary>Whether an action would do anything now: what the widget greys out.</summary>
	public bool IsEnabled(DraftAction action)
	{
		if (!IsOpen)
		{
			return false;
		}

		LoadoutSelection kit = Kit;
		int pouches = kit.Magazines + kit.Grenades;
		return action switch
		{
			>= DraftAction.Primary1 and <= DraftAction.Primary4 =>
				PrimaryIndex(action) < _catalog.Primaries.Length && _catalog.Primaries[PrimaryIndex(action)] != kit.Large,
			DraftAction.FewerMagazines => kit.Magazines > 0,
			DraftAction.MoreMagazines => kit.HasPrimary && pouches < Loadout.PouchSlots,
			DraftAction.FewerGrenades => kit.Grenades > 0,
			DraftAction.MoreGrenades => kit.Grenades < SimConfig.MaxGrenades && pouches < Loadout.PouchSlots,
			DraftAction.LighterVest => kit.Armour > BodyArmourTier.None,
			DraftAction.HeavierVest => kit.Armour < BodyArmourTier.Heavy,
			DraftAction.Take => !Awaiting,
			DraftAction.Revert => kit != Original,
			DraftAction.Default => kit != Sane(_catalog.DefaultKit),
			DraftAction.Close => true,
			_ => false,
		};
	}

	/// <summary>Does what a key or a button asks, if it is enabled, and says what the panel has to do next.</summary>
	public DraftOutcome Apply(DraftAction action)
	{
		if (!IsEnabled(action))
		{
			return DraftOutcome.None;
		}

		LoadoutSelection kit = Kit;
		switch (action)
		{
			case >= DraftAction.Primary1 and <= DraftAction.Primary4:
				// No primary carries no magazines (§4.1): Edit's sanitizing drops them, and
				// the pouches they were in are free.
				kit.Large = _catalog.Primaries[PrimaryIndex(action)];
				return Edit(kit);

			case DraftAction.FewerMagazines: kit.Magazines--; return Edit(kit);
			case DraftAction.MoreMagazines: kit.Magazines++; return Edit(kit);
			case DraftAction.FewerGrenades: kit.Grenades--; return Edit(kit);
			case DraftAction.MoreGrenades: kit.Grenades++; return Edit(kit);
			case DraftAction.LighterVest: kit.Armour--; return Edit(kit);
			case DraftAction.HeavierVest: kit.Armour++; return Edit(kit);
			case DraftAction.Revert: return Edit(Original);
			case DraftAction.Default: return Edit(Sane(_catalog.DefaultKit));

			case DraftAction.Take:
				LastRefusal = KitRefusal.None;
				if (Mode == DraftMode.NextSpawn)
				{
					Close();
					return DraftOutcome.SetNextSpawn;
				}

				Awaiting = true;
				return DraftOutcome.AskLocker;

			case DraftAction.Close:
				Close();
				return DraftOutcome.Close;
		}

		return DraftOutcome.None;
	}

	/// <summary>A click on a primary's radio button: the <paramref name="index"/>th in the panel's order.</summary>
	public DraftOutcome ChoosePrimary(int index) =>
		index >= 0 && index < 4 ? Apply((DraftAction)((int)DraftAction.Primary1 + index)) : DraftOutcome.None;

	/// <summary>A click on a vest's radio button.</summary>
	public DraftOutcome ChooseVest(BodyArmourTier tier)
	{
		if (!IsOpen || tier > BodyArmourTier.Heavy || tier == Kit.Armour)
		{
			return DraftOutcome.None;
		}

		LoadoutSelection kit = Kit;
		kit.Armour = tier;
		return Edit(kit);
	}

	/// <summary>
	/// The locker's answer to the kit asked for (<c>ServerKitResult</c>, §6.2): taken
	/// closes the panel; a refusal goes in the footer and leaves the panel open, with
	/// the kit as it was asked for and Enter enabled again. An answer to nothing — a
	/// panel closed and opened again while one was on its way — is ignored.
	/// </summary>
	public DraftOutcome Answer(KitRefusal refusal)
	{
		if (!IsOpen || !Awaiting)
		{
			return DraftOutcome.None;
		}

		Awaiting = false;
		if (refusal == KitRefusal.None)
		{
			Close();
			return DraftOutcome.Close;
		}

		LastRefusal = refusal;
		return DraftOutcome.Edited;
	}

	/// <summary>
	/// Whether the panel is still wanted (L-D12): at a locker, while the player is alive
	/// on the ground and in the locker's reach; from the death screen, until they are
	/// back on the field. Either closes for the role menu, a strategist's chair and the
	/// end of the round, whose scoreboard takes the middle of the screen.
	/// </summary>
	public bool StillWanted(in DraftSituation situation)
	{
		if (!IsOpen)
		{
			return false;
		}

		if (!situation.GroundForce || situation.AwaitingRole || situation.RoundEnded)
		{
			return false;
		}

		return Mode == DraftMode.NextSpawn
			? !situation.Alive
			: situation.Alive && LockerSupply.InReach(situation.LockerDistance);
	}

	// ---- read-outs (§7.2), from §4's functions ---------------------------------

	public float WeightKg => Loadout.WeightKg(Kit, _catalog.Carry);

	public float SpeedScale => Loadout.SpeedScale(WeightKg, _catalog.ReferenceKg);

	public float WalkSpeed => _catalog.WalkSpeed * SpeedScale;

	public float SprintSpeed => _catalog.SprintSpeed * SpeedScale;

	/// <summary>The primary's spare rounds when full: a pouch's rounds for each magazine pouch.</summary>
	public int Reserve => Loadout.ReserveFor(Kit, _catalog.CarryOf(Kit.Large));

	/// <summary>Hits of the reference round that kill the player from full, in this kit's vest.</summary>
	public int HitsToKill => BodyArmour.HitsToKill(Kit.Armour, _catalog.ReferenceDamage, explosive: false);

	/// <summary>The same with no vest.</summary>
	public int HitsToKillBare => BodyArmour.HitsToKill(BodyArmourTier.None, _catalog.ReferenceDamage, explosive: false);

	public string Title => Mode == DraftMode.Locker ? "WEAPON LOCKER" : "NEXT SPAWN";

	/// <summary>A primary's radio label: its key, its name and its weight — <c>2 rifle 3.6 kg</c>; none has no weight.</summary>
	public string PrimaryLabel(int index)
	{
		if (index < 0 || index >= _catalog.Primaries.Length)
		{
			return string.Empty;
		}

		byte id = _catalog.Primaries[index];
		return id == Loadout.NoPrimary
			? $"{index + 1} none"
			: $"{index + 1} {_catalog.NameOf(id)} {_catalog.CarryOf(id).WeightKg:0.0} kg";
	}

	/// <summary>A vest's radio label: <c>medium 6 kg</c>; none has no weight.</summary>
	public static string VestLabel(BodyArmourTier tier) =>
		tier == BodyArmourTier.None
			? "none"
			: $"{Loadout.ArmourNames[(int)tier]} {Loadout.VestWeightKg(tier):0} kg";

	/// <summary>
	/// The nine pouches, magazines first, then grenades, then empty: <c>▣▣▣▣▣◆◆◆□</c>.
	/// Only the counts mean anything; which pouch is which is not a choice.
	/// </summary>
	public string PouchCells
	{
		get
		{
			var cells = new StringBuilder(Loadout.PouchSlots);
			for (int i = 0; i < Loadout.PouchSlots; i++)
			{
				cells.Append(i < Kit.Magazines ? '▣' : i < Kit.Magazines + Kit.Grenades ? '◆' : '□');
			}

			return cells.ToString();
		}
	}

	/// <summary><c>LOAD 15.3 kg   SPEED 88 %   walk 4.4 · sprint 7.0 m/s</c>.</summary>
	public string LoadLine =>
		$"LOAD {WeightKg:0.0} kg   SPEED {SpeedScale * 100f:0} %   walk {WalkSpeed:0.0} · sprint {SprintSpeed:0.0} m/s";

	/// <summary>
	/// <c>VEST 60 pts, absorbs 50 %   rifle hits to kill 8 (5 without)</c>, or with no
	/// vest <c>VEST none   rifle hits to kill 5</c>.
	/// </summary>
	public string VestLine =>
		Kit.Armour == BodyArmourTier.None
			? $"VEST none   {_catalog.ReferenceRound} hits to kill {HitsToKillBare}"
			: $"VEST {BodyArmour.MaxPoints(Kit.Armour)} pts, absorbs {BodyArmour.Share(Kit.Armour) * 100f:0} %"
				+ $"   {_catalog.ReferenceRound} hits to kill {HitsToKill} ({HitsToKillBare} without)";

	/// <summary>
	/// <c>AMMO 30 + 150 rifle · 3 grenades</c>: the magazine and the spare rounds a full
	/// kit carries, and its grenades; <c>AMMO pistol only · 3 grenades</c> with no primary.
	/// </summary>
	public string AmmoLine
	{
		get
		{
			string grenades = $"{Kit.Grenades} grenade{(Kit.Grenades == 1 ? string.Empty : "s")}";
			return Kit.HasPrimary
				? $"AMMO {_catalog.MagazineOf(Kit.Large)} + {Reserve} {_catalog.NameOf(Kit.Large)} · {grenades}"
				: $"AMMO pistol only · {grenades}";
		}
	}

	/// <summary>The label of the button at the foot of the panel for an action; what Enter and Backspace say depends on the mode.</summary>
	public string KeyLabel(DraftAction action) => action switch
	{
		DraftAction.Take => Mode == DraftMode.Locker ? "[Enter] take this kit" : "[Enter] keep for the next spawn",
		DraftAction.Revert => Mode == DraftMode.Locker ? "[Backspace] what I carry" : "[Backspace] as it was",
		DraftAction.Default => "[D] default",
		DraftAction.Close => "[E] close",
		_ => string.Empty,
	};

	/// <summary>The foot of the panel in one line.</summary>
	public string KeysLine => string.Join("   ", KeyLabel(DraftAction.Take), KeyLabel(DraftAction.Revert),
		KeyLabel(DraftAction.Default), KeyLabel(DraftAction.Close));

	/// <summary>
	/// The panel's last line: the answer being waited for, the refusal the locker gave,
	/// or nothing.
	/// </summary>
	public string StatusLine =>
		Awaiting ? "asking the locker…"
		: LastRefusal != KitRefusal.None ? $"refused ({KitRequest.CodeOf(LastRefusal)}): {Explain(LastRefusal)}"
		: string.Empty;

	/// <summary>What a refusal means to the person who was refused it.</summary>
	public static string Explain(KitRefusal refusal) => refusal switch
	{
		KitRefusal.Dead => "you are not on the field",
		KitRefusal.NotAtLocker => "too far from the locker",
		KitRefusal.Mounted => "get off the gun first",
		KitRefusal.Carrying => "put down what you are carrying first",
		KitRefusal.Invalid => "that kit breaks a loadout rule",
		KitRefusal.TooSoon => "one kit a quarter-second; take it again",
		_ => string.Empty,
	};

	private DraftOutcome Edit(LoadoutSelection kit)
	{
		kit = Sane(kit);
		if (kit == Kit)
		{
			return DraftOutcome.None;
		}

		Kit = kit;
		LastRefusal = KitRefusal.None;
		return DraftOutcome.Edited;
	}

	/// <summary>A kit as the rules allow it, carrying the hammer and the pistol every kit carries.</summary>
	private LoadoutSelection Sane(LoadoutSelection kit)
	{
		LoadoutSelection sane = Loadout.Sanitize(kit, _catalog.LargeWeapons, _catalog.DefaultKit.Large);
		sane.Melee = _catalog.DefaultKit.Melee;
		sane.Sidearm = _catalog.DefaultKit.Sidearm;
		return sane;
	}

	private static int PrimaryIndex(DraftAction action) => action - DraftAction.Primary1;
}
