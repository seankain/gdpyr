using Gdpyr.Fps;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Core;

/// <summary>
/// Every crew vehicle — every vehicle the ground force drives — in a fixed order
/// (docs/GROUND_VEHICLES.md §5.8).
///
/// The contract <see cref="UnitCatalog"/> and <see cref="WeaponCatalog"/> keep, for the
/// reason they keep it: the vehicle record names its kind in two bits, the kiosk's
/// request in a byte, and either means nothing unless the server and every client
/// resolve it to the same vehicle. The indices are <see cref="CrewVehicleKind"/>, which
/// the engine-free rules name them by. Appending is safe; reordering is a wire break.
///
/// Nothing loads it yet: GV2 does, from the crew vehicle manager's <c>_Ready</c>.
/// </summary>
public static class CrewVehicleCatalog
{
	/// <summary>Index is the wire id, and is <see cref="CrewVehicleKind"/>. Do not reorder.</summary>
	private static readonly string[] Paths =
	{
		"res://Vehicles/four_by_four.tres", // 0 — CrewVehicleKind.FourByFour
		"res://Vehicles/helicopter.tres",   // 1 — CrewVehicleKind.Helicopter
	};

	public const byte FourByFour = (byte)CrewVehicleKind.FourByFour;

	public const byte Helicopter = (byte)CrewVehicleKind.Helicopter;

	public static CrewVehicleDefinition[] Definitions { get; private set; } =
		System.Array.Empty<CrewVehicleDefinition>();

	public static bool IsLoaded { get; private set; }

	public static int Count => Definitions.Length;

	/// <summary>Loads the catalog, once, on servers and clients alike: a client drives and draws them.</summary>
	public static void Load()
	{
		if (IsLoaded)
		{
			return;
		}

		var definitions = new CrewVehicleDefinition[Paths.Length];
		for (int i = 0; i < Paths.Length; i++)
		{
			var definition = GD.Load<CrewVehicleDefinition>(Paths[i]);
			if (definition == null)
			{
				// Fatal on purpose, as for a missing weapon or unit: a build that disagrees
				// about what id `i` is will put the wrong vehicle on the pad.
				GD.PushError($"[vehicles] crew vehicle {i} missing at {Paths[i]}");
				continue;
			}

			if (definition.Kind != i)
			{
				GD.PushError($"[vehicles] {Paths[i]} says it is kind {definition.Kind}, and sits at {i}");
			}

			definitions[i] = definition;
		}

		Definitions = definitions;
		IsLoaded = true;
	}

	public static CrewVehicleDefinition Definition(byte id) => id < Definitions.Length ? Definitions[id] : null;

	public static bool IsKnown(byte id) => id < Definitions.Length && Definitions[id] != null;

	/// <summary>The kind's name as players, the HUD and the agent API say it: <c>4x4</c>, <c>helicopter</c>.</summary>
	public static string NameOf(byte id) => Definition(id)?.Name.ToString() ?? $"#{id}";
}
