using System;

namespace Gdpyr.Sim.AiDebug;

/// <summary>
/// Where each task of a domain goes in the spectator's graph view (docs/AI_DEBUG.md
/// §4.2): depth across, leaves down, every compound task level with the middle of
/// its children — a tidy tree on its side. Sideways because the domains here are
/// shallow and wide: five levels and twenty-odd leaves fit a panel on their side,
/// and do not fit it upright.
///
/// Engine-free so that the layout is a question for <c>dotnet test</c>; the view
/// (<c>Scripts/Ui/Spectator/HtnGraphView.cs</c>) only scales it and draws.
/// </summary>
public sealed class HtnGraphLayout
{
	private HtnGraphLayout(int count)
	{
		Visible = new bool[count];
		Column = new int[count];
		Row = new float[count];
	}

	/// <summary>Per node: drawn at all. A folded task's descendants are not.</summary>
	public bool[] Visible { get; }

	/// <summary>Per node: its depth.</summary>
	public int[] Column { get; }

	/// <summary>Per node: its row, fractional for a compound task centred on its children.</summary>
	public float[] Row { get; }

	public int Rows { get; private set; }

	public int Columns { get; private set; }

	/// <summary>
	/// Lays <paramref name="map"/> out. With <paramref name="collapse"/>, a compound task
	/// off the plan's path (<see cref="HtnTrace.PathMask"/>) is drawn but its children
	/// are not — the same fold the text tree makes.
	/// </summary>
	public static HtnGraphLayout Compute(HtnDomainMap map, HtnTrace trace, bool collapse)
	{
		if (map == null || map.Count == 0)
		{
			return new HtnGraphLayout(0);
		}

		bool[] path = trace?.PathMask(map) ?? new bool[map.Count];
		var layout = new HtnGraphLayout(map.Count);
		int next = 0;
		layout.Place(map, path, collapse, 0, ref next);
		layout.Rows = next;
		return layout;
	}

	private float Place(HtnDomainMap map, bool[] path, bool collapse, int index, ref int next)
	{
		HtnNode node = map[index];
		Visible[index] = true;
		Column[index] = node.Depth;
		Columns = Math.Max(Columns, node.Depth + 1);

		bool expanded = index == 0 || !collapse || path[index];
		if (!expanded || node.Children.Count == 0)
		{
			Row[index] = next++;
			return Row[index];
		}

		float first = 0f;
		float last = 0f;
		for (int i = 0; i < node.Children.Count; i++)
		{
			float row = Place(map, path, collapse, node.Children[i], ref next);
			if (i == 0)
			{
				first = row;
			}

			last = row;
		}

		Row[index] = (first + last) * 0.5f;
		return Row[index];
	}
}
