using System;
using Gdpyr.Core;
using Gdpyr.Fps;
using Gdpyr.Match;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Godot;

namespace Gdpyr.Agent;

/// <summary>
/// Fills the optional feature planes for a strategist seat
/// (docs/AGENT_API.md §6.3).
///
/// Three of the four channels are a scatter over state the vector already
/// carries, at the same fog: a policy asking for planes is asking for the same
/// information laid out where a convolution can use it, not for more of it. The
/// fourth — passability — is the map's own geometry and does not move, so it is
/// probed once and then re-used for every observation of every seat for the rest
/// of the round.
///
/// Planes are a strategist affordance and nothing else. A ground policy's spatial
/// signal is the ray fan (§6.1): it is egocentric, it is sixteen floats rather
/// than four thousand, and a top-down grid of the whole map is not what a body in
/// a corridor needs.
/// </summary>
public sealed class AgentPlaneView
{
	/// <summary>Where the passability probe starts, above anything the greybox map has.</summary>
	private const float ProbeHeightMeters = 80f;

	/// <summary>How far below the probe a floor still counts as one.</summary>
	private const float ProbeDepthMeters = 120f;

	private readonly float[] _grid = new float[AgentFeaturePlanes.Floats];
	private bool _passabilityProbed;

	/// <summary>Cells the probe found ground under. For the debug HUD and the trace.</summary>
	public int PassableCells { get; private set; }

	/// <summary>The packed grid, channel-last, as the last <see cref="Fill"/> left it.</summary>
	public ReadOnlySpan<float> Grid => _grid;

	/// <summary>Forgets the map probe. Called when a round resets, in case the map changed under it.</summary>
	public void Invalidate() => _passabilityProbed = false;

	/// <summary>
	/// Fills the grid for <paramref name="peerId"/>'s side. Returns false when the
	/// seat has no round to look at.
	/// </summary>
	public bool Fill(int peerId, uint tick, in AgentLimits limits)
	{
		CombatManager combat = CombatManager.Instance;
		PlayerCombat player = combat?.Find(peerId);
		UnitManager units = UnitManager.Instance;
		if (combat == null || player == null || units == null)
		{
			return false;
		}

		ProbePassability();
		AgentFeaturePlanes.ClearDynamic(_grid);

		for (int i = 0; i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (unit == null || !unit.IsAlive || unit.Team != player.Team)
			{
				continue;
			}

			AgentFeaturePlanes.Scatter(_grid, AgentFeaturePlanes.OwnUnits, unit.GlobalPosition.X,
				unit.GlobalPosition.Z);
		}

		VisibilityService fog = combat.Visibility;
		for (int i = 0; i < combat.PlayerCount; i++)
		{
			PlayerCombat contact = combat.PlayerAt(i);
			if (contact?.Character == null || contact.Team == player.Team)
			{
				continue;
			}

			// The same fog the vector's contacts are behind: a plane that drew an
			// enemy the seat cannot see would be a second, quieter observation space
			// (docs/AGENT_API.md §6.2).
			if (limits.Omniscient)
			{
				AgentFeaturePlanes.Scatter(_grid, AgentFeaturePlanes.Contacts,
					contact.Character.SimPosition.X, contact.Character.SimPosition.Z);
			}
			else if (fog != null && fog.TryContact(contact.PeerId, tick, out Vector3 at, out _))
			{
				AgentFeaturePlanes.Scatter(_grid, AgentFeaturePlanes.Contacts, at.X, at.Z);
			}
		}

		EconomyService economy = combat.Economy;
		for (int i = 0; i < economy.NodeCount; i++)
		{
			ResourceNode node = economy.NodeAt(i);
			if (node == null)
			{
				continue;
			}

			AgentFeaturePlanes.Set(_grid, AgentFeaturePlanes.NodeOwnership, node.GlobalPosition.X,
				node.GlobalPosition.Z, AgentFeaturePlanes.OwnershipValue((byte)node.Capture.Owner));
		}

		return true;
	}

	/// <summary>
	/// One downward ray per cell, once per map: is there ground here at all.
	///
	/// A thousand rays is far too many to spend per observation and nothing at all
	/// to spend once — the map does not move, and a policy asking "can my units walk
	/// there" is asking about geometry rather than about this tick. It is
	/// deliberately the cheap question: a cell with a floor under it reads
	/// passable, whether or not a tank would fit between the crates standing on it.
	/// </summary>
	private void ProbePassability()
	{
		if (_passabilityProbed)
		{
			return;
		}

		PhysicsDirectSpaceState3D space =
			(Engine.GetMainLoop() as SceneTree)?.Root?.World3D?.DirectSpaceState;
		if (space == null)
		{
			return;
		}

		_passabilityProbed = true;
		PassableCells = 0;

		const float step = (AgentFeaturePlanes.HalfExtentMeters * 2f) / AgentFeaturePlanes.Size;
		for (int row = 0; row < AgentFeaturePlanes.Size; row++)
		{
			for (int column = 0; column < AgentFeaturePlanes.Size; column++)
			{
				float x = (-AgentFeaturePlanes.HalfExtentMeters) + ((column + 0.5f) * step);
				float z = (-AgentFeaturePlanes.HalfExtentMeters) + ((row + 0.5f) * step);

				var from = new Vector3(x, ProbeHeightMeters, z);
				var to = new Vector3(x, ProbeHeightMeters - ProbeDepthMeters, z);

				PhysicsRayQueryParameters3D query =
					PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.World);
				bool ground = space.IntersectRay(query).Count > 0;

				_grid[AgentFeaturePlanes.Index(row, column, AgentFeaturePlanes.Passability)] =
					ground ? 1f : 0f;

				if (ground)
				{
					PassableCells++;
				}
			}
		}

		GD.Print($"[agent] feature planes probed | {PassableCells}/{AgentFeaturePlanes.Size * AgentFeaturePlanes.Size}"
			+ " cells have ground");
	}
}
