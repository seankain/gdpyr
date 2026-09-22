using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Core;

/// <summary>
/// Every structure a builder can put up, in a fixed order (docs/NETCODE.md §10.5).
///
/// The contract <see cref="UnitCatalog"/> and <see cref="WeaponCatalog"/> keep, for
/// the reason they keep it: a structure's spawn message names its kind with one
/// byte, and that byte means nothing unless the server and every client resolve
/// it to the same structure. The indices are <see cref="StructureKinds"/>, which
/// the engine-free code names them by. Appending is safe; reordering is a wire
/// break that turns somebody's sandbags into a pillbox.
/// </summary>
public static class StructureCatalog
{
	/// <summary>Index is the wire id, and is <see cref="StructureKinds"/>. Do not reorder.</summary>
	private static readonly string[] Paths =
	{
		"res://Structures/pillbox.tres",      // 0 — StructureKinds.Pillbox
		"res://Structures/sandbag_wall.tres", // 1 — StructureKinds.SandbagWall
		"res://Structures/sniper_tower.tres", // 2 — StructureKinds.SniperTower
	};

	public static StructureDefinition[] Definitions { get; private set; } =
		System.Array.Empty<StructureDefinition>();

	/// <summary>Each structure's boxes, parallel to <see cref="Definitions"/>.</summary>
	public static StructureShape[] Shapes { get; private set; } = System.Array.Empty<StructureShape>();

	public static bool IsLoaded { get; private set; }

	public static int Count => Definitions.Length;

	/// <summary>Loads the catalog, once, on servers and clients alike: a client needs the boxes to collide with.</summary>
	public static void Load()
	{
		if (IsLoaded)
		{
			return;
		}

		var definitions = new StructureDefinition[Paths.Length];
		var shapes = new StructureShape[Paths.Length];

		for (int i = 0; i < Paths.Length; i++)
		{
			var definition = GD.Load<StructureDefinition>(Paths[i]);
			if (definition == null)
			{
				GD.PushError($"[rts] structure {i} missing at {Paths[i]}");
				continue;
			}

			definitions[i] = definition;
			shapes[i] = definition.ToShape();
		}

		Definitions = definitions;
		Shapes = shapes;
		IsLoaded = true;
	}

	public static StructureDefinition Definition(byte id) => id < Definitions.Length ? Definitions[id] : null;

	public static StructureShape ShapeOf(byte id) => id < Shapes.Length ? Shapes[id] : default;

	public static bool IsBuildable(byte id) => id < Definitions.Length && Definitions[id] != null;

	public static int CostOf(byte id) => Definition(id)?.Cost ?? int.MaxValue;

	public static string NameOf(byte id) => Definition(id)?.Name.ToString() ?? $"#{id}";
}
