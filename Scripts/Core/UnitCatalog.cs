using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Core;

/// <summary>
/// Every unit in the game, in a fixed order.
///
/// The same contract as <see cref="WeaponCatalog"/>, for the same reason: a unit's
/// spawn message names its kind with one byte (docs/NETCODE.md §6.1), and that
/// byte means nothing unless the server and every client resolve it to the same
/// unit. Appending is safe; reordering or removing is a wire break that will
/// silently turn somebody's riflemen into tanks.
///
/// One entry today. M5's three tiers — Infantry, Technical, Tank
/// (docs/IMPLEMENTATION_PLAN.md §M5) — are two more lines in the table and two
/// more resource files, which is the point of the table.
/// </summary>
public static class UnitCatalog
{
	/// <summary>Index is the wire id. Do not reorder.</summary>
	private static readonly string[] Paths =
	{
		"res://Units/infantry.tres",  // 0 — T0 rifleman
		"res://Units/technical.tres", // 1 — T1 gun truck
		"res://Units/tank.tres",      // 2 — T2 tank
		"res://Units/builder.tres",   // 3 — builder, not a tier
	};

	public const byte Infantry = 0;

	/// <summary>
	/// T1. Fast, thin, and the only unit that sees appreciably further than it
	/// shoots — which is what makes it a scout rather than a cheaper tank, and the
	/// unit the fog of war argues for (docs/IMPLEMENTATION_PLAN.md §M4, §M5).
	/// </summary>
	public const byte Technical = 1;

	/// <summary>T2. Slow, blind at range, and the only thing on the field that survives being shot at.</summary>
	public const byte Tank = 2;

	/// <summary>
	/// The one unit that fights nothing it can avoid: it puts up pillboxes, sandbag
	/// walls and sniper towers (docs/NETCODE.md §10.5). Not a tier — it is
	/// deliberately absent from <see cref="Tiers"/>, which is what the computer
	/// strategist chooses its army from.
	/// </summary>
	public const byte Builder = 3;

	/// <summary>The three fighting tiers in order, which is the order the strategist's build keys are in.</summary>
	public static readonly byte[] Tiers = { Infantry, Technical, Tank };

	public static UnitDefinition[] Definitions { get; private set; } = System.Array.Empty<UnitDefinition>();

	/// <summary>The brain's view of each unit, parallel to <see cref="Definitions"/>.</summary>
	public static UnitTraits[] Traits { get; private set; } = System.Array.Empty<UnitTraits>();

	public static bool IsLoaded { get; private set; }

	public static int Count => Definitions.Length;

	/// <summary>
	/// Loads the catalog. Called once, from the unit manager's <c>_Ready</c>, on
	/// servers and clients alike: a client needs the mesh and the colour, and a
	/// headless server needs the numbers (docs/DEPLOYMENT.md §2).
	/// </summary>
	public static void Load()
	{
		if (IsLoaded)
		{
			return;
		}

		var definitions = new UnitDefinition[Paths.Length];
		var traits = new UnitTraits[Paths.Length];

		for (int i = 0; i < Paths.Length; i++)
		{
			var definition = GD.Load<UnitDefinition>(Paths[i]);
			if (definition == null)
			{
				// Fatal on purpose, exactly as for a missing weapon: a build that
				// disagrees about what id `i` is will spawn the wrong thing and nobody
				// will be able to account for it.
				GD.PushError($"[rts] unit {i} missing at {Paths[i]}");
				continue;
			}

			definitions[i] = definition;
			traits[i] = definition.ToTraits();
		}

		Definitions = definitions;
		Traits = traits;
		IsLoaded = true;
	}

	public static UnitDefinition Definition(byte id) => id < Definitions.Length ? Definitions[id] : null;

	public static UnitTraits TraitsFor(byte id) => id < Traits.Length ? Traits[id] : default;

	public static string NameOf(byte id) => Definition(id)?.Name.ToString() ?? $"#{id}";

	public static bool IsBuildable(byte id) => id < Definitions.Length && Definitions[id] != null;

	/// <summary>Whether units of this kind can be sent to put a structure up.</summary>
	public static bool CanConstruct(byte id) => Definition(id)?.CanConstruct ?? false;

	/// <summary>
	/// What the cheapest buildable unit costs, or <see cref="int.MaxValue"/> when
	/// nothing can be built at all.
	///
	/// This is half of the strategist's defeat condition — "points below the
	/// cheapest unit" (docs/IMPLEMENTATION_PLAN.md §M5) — and it is derived rather
	/// than authored so that adding a fourth tier cannot leave a constant behind.
	/// </summary>
	public static int CheapestCost
	{
		get
		{
			int cheapest = int.MaxValue;
			for (int i = 0; i < Definitions.Length; i++)
			{
				UnitDefinition definition = Definitions[i];
				if (definition != null && definition.Cost < cheapest)
				{
					cheapest = definition.Cost;
				}
			}
			return cheapest;
		}
	}

	public static int CostOf(byte id) => Definition(id)?.Cost ?? int.MaxValue;

	/// <summary>
	/// Clamps a unit id that arrived from a client. A strategist picks what to
	/// queue, so "unit 200" is something the server has to answer for
	/// (docs/NETCODE.md §1).
	/// </summary>
	public static bool TrySanitize(byte requested, out byte id)
	{
		id = IsBuildable(requested) ? requested : Infantry;
		return IsBuildable(id);
	}
}
