using System.Collections.Generic;
using Gdpyr.Fps;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Match;

/// <summary>
/// The strategist's income, and the ground force's way of stopping it
/// (docs/IMPLEMENTATION_PLAN.md §M5).
///
/// Until M5 the point pool was filled once at the start of a round and only ever
/// shrank, which makes a strategist's whole game one long decision made in the
/// first minute. Resource nodes turn it into a rate: hold ground, be paid for it,
/// and lose the payment the moment somebody walks onto it.
///
/// Server-only, like every other authority here. A node's capture rules are
/// engine-free and tested (<see cref="CaptureState"/>); this is the part that
/// counts who is standing where, which is the registry walk §1 of the plan insists
/// on doing without a scene scan — the roster and the unit array are both already
/// in hand.
/// </summary>
public sealed class EconomyService
{
	private readonly List<ResourceNode> _nodes = new();

	/// <summary>Resource nodes on the map, ordered by name: the index is the wire id.</summary>
	public int NodeCount => _nodes.Count;

	public ResourceNode NodeAt(int index) => index >= 0 && index < _nodes.Count ? _nodes[index] : null;

	/// <summary>Nodes the strategist holds. For the debug HUD and the win condition.</summary>
	public int StrategistNodes { get; private set; }

	/// <summary>Nodes the ground force holds.</summary>
	public int GroundNodes { get; private set; }

	/// <summary>Nodes with somebody from both sides standing on them.</summary>
	public int ContestedNodes { get; private set; }

	/// <summary>Points the nodes have paid the strategist this round. For the HUD and M6's CSV.</summary>
	public int IncomePaid { get; private set; }

	/// <summary>The tick the occupancy was last scanned on.</summary>
	public uint LastScanTick { get; private set; }

	/// <summary>
	/// Finds the map's nodes. Called once, on the first tick, for the same reason
	/// the barracks and the spawn points are: an autoload is ready before the map
	/// is.
	/// </summary>
	public void Collect(SceneTree tree)
	{
		_nodes.Clear();
		if (tree == null)
		{
			return;
		}

		foreach (Node node in tree.GetNodesInGroup(ResourceNode.Group))
		{
			if (node is ResourceNode resource)
			{
				_nodes.Add(resource);
			}
		}

		// Sorted by name and capped, exactly as the barracks are: the index is what a
		// state message names, so every peer has to derive the same one from the same
		// scene (docs/NETCODE.md §6.3).
		_nodes.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
		if (_nodes.Count > SimConfig.MaxResourceNodes)
		{
			GD.PushWarning($"[economy] {_nodes.Count} resource nodes; only the first"
				+ $" {SimConfig.MaxResourceNodes} are addressable on the wire");
			_nodes.RemoveRange(SimConfig.MaxResourceNodes, _nodes.Count - SimConfig.MaxResourceNodes);
		}
	}

	/// <summary>
	/// One scan, when one is due. Called every server tick by
	/// <see cref="CombatManager.ServerPostTick"/>, after the units have moved.
	/// </summary>
	public void ServerTick(uint tick, CombatManager combat, UnitManager units)
	{
		if (_nodes.Count == 0 || tick % SimConfig.CaptureScanIntervalTicks != 0)
		{
			return;
		}

		LastScanTick = tick;

		for (int i = 0; i < _nodes.Count; i++)
		{
			ResourceNode node = _nodes[i];
			Count(node, combat, units, out int ground, out int strategist);

			int income = node.Capture.Tick(tick, ground, strategist, node.Rules);
			if (income <= 0)
			{
				continue;
			}

			// Straight into the ledger the barracks spends from: income and a refund
			// are the same thing to it, which is what ResourceLedger.Add was left here
			// for in M3.
			combat?.Match.Strategist.Add(income);
			IncomePaid += income;
		}

		Tally();
	}

	/// <summary>
	/// Repaints every node for whoever holds it and re-counts who holds what.
	/// Presentation and a tally, on servers and clients alike — a client's counts
	/// come from the same <see cref="CaptureState"/>s, which it is told about
	/// rather than computing.
	/// </summary>
	public void Refresh()
	{
		for (int i = 0; i < _nodes.Count; i++)
		{
			_nodes[i].Repaint();
		}

		Tally();
	}

	private void Tally()
	{
		int strategist = 0;
		int ground = 0;
		int contested = 0;

		for (int i = 0; i < _nodes.Count; i++)
		{
			CaptureState capture = _nodes[i].Capture;

			if (capture.Contested)
			{
				contested++;
			}

			if (capture.IsHeldBy(NodeHolder.Strategist))
			{
				strategist++;
			}
			else if (capture.IsHeldBy(NodeHolder.GroundForce))
			{
				ground++;
			}
		}

		StrategistNodes = strategist;
		GroundNodes = ground;
		ContestedNodes = contested;
	}

	/// <summary>Hands every node back to nobody, for the next round.</summary>
	public void Reset()
	{
		for (int i = 0; i < _nodes.Count; i++)
		{
			_nodes[i].Capture.Reset();
		}

		StrategistNodes = 0;
		GroundNodes = 0;
		ContestedNodes = 0;
		IncomePaid = 0;
	}

	/// <summary>
	/// Who is standing on one node. Living bodies only: a corpse waiting out its
	/// respawn does not hold ground, and neither does a strategist, who has no body
	/// on the field to stand with.
	/// </summary>
	private static void Count(ResourceNode node, CombatManager combat, UnitManager units, out int ground,
		out int strategist)
	{
		ground = 0;
		strategist = 0;

		for (int i = 0; combat != null && i < combat.PlayerCount; i++)
		{
			PlayerCombat player = combat.PlayerAt(i);
			if (player?.Character == null || !player.IsAlive || player.Team != Team.GroundForce)
			{
				continue;
			}

			if (node.Covers(player.Character.SimPosition))
			{
				ground++;
			}
		}

		for (int i = 0; units != null && i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (unit == null || !unit.IsAlive || unit.Team != Team.Strategist)
			{
				continue;
			}

			if (node.Covers(unit.GlobalPosition))
			{
				strategist++;
			}
		}
	}
}
