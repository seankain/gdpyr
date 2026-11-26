using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Gdpyr.Core;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Fps;

/// <summary>
/// A ladder on a map (docs/LADDERS.md §3). Stand the node at the middle of the ladder's
/// foot, looking the way its climber looks — at the wall, the scaffold or the hatch edge it
/// is fixed to — and set <see cref="Height"/> to its landing, the floor it leads onto beyond
/// its face. Its +Z is then the face's normal, out towards the climber
/// (<see cref="LadderSpan.Normal"/>).
///
/// What it adds, from its transform and its numbers, so a map only places it:
/// <list type="bullet">
/// <item>its <see cref="LadderSpan"/> to <see cref="All"/>, which the movement states climb by
/// (<see cref="PlayerClimbingState"/>) — the same list, in tree order, in every process that
/// loads the same map, which is what keeps a client's prediction of a climb the server's;</item>
/// <item>a thin box on the world layer behind its face, foot to landing, so a body cannot walk
/// through the rungs and a navigation mesh goes round them (<see cref="Solid"/>);</item>
/// <item>a navigation link from in front of its foot to over its landing, on
/// <see cref="NavigationLayer"/>, which ground bots path on and units do not;</item>
/// <item>its stiles and rungs, drawn, except on a dedicated server.</item>
/// </list>
///
/// Placed once and never moved: the span is taken as the node enters the tree.
/// </summary>
public partial class Ladder : Node3D
{
	public const string Group = "ladder";

	/// <summary>
	/// The navigation layer (2) a ladder's link is on. Ground bots' agents path on it and on
	/// the ground's layer 1 (<c>BotPilot</c>); a unit's agent is on layer 1 alone, because a unit
	/// cannot climb (docs/LADDERS.md §5).
	/// </summary>
	public const uint NavigationLayer = 1u << 1;

	/// <summary>From the foot to the landing [m].</summary>
	[Export]
	public float Height = 3.5f;

	/// <summary>Between the stiles [m].</summary>
	[Export]
	public float Width = 0.5f;

	/// <summary>How far the stiles stand up past the landing, to be held going over it. Drawn only.</summary>
	[Export]
	public float StilesAboveMeters = 1f;

	/// <summary>
	/// A box behind the face that stops bodies, rounds and the navigation mesh. Off for a ladder
	/// already against a wall, which stops them itself.
	/// </summary>
	[Export]
	public bool Solid = true;

	/// <summary>
	/// How far out in front of the foot its navigation link starts: past where the ladder can be
	/// taken from (<see cref="Ladders.ReachMeters"/>), so a bot walks into it squarely.
	/// </summary>
	[Export]
	public float LinkOutMeters = 1.8f;

	/// <summary>How far onto the landing, past the face, its navigation link ends.</summary>
	[Export]
	public float LinkOverMeters = 1.6f;

	/// <summary>The yellow of the drawn ladder.</summary>
	private static readonly Color Paint = new(0.86f, 0.68f, 0.12f);

	private const float StileSide = 0.06f;
	private const float RungThickness = 0.035f;
	private const float RungSpacing = 0.3f;
	private const float BodyDepth = 0.08f;

	private static readonly List<LadderSpan> Spans = new();
	private static readonly List<Ladder> Nodes = new();

	/// <summary>Every ladder in the tree, in tree order: what the movement states take and climb.</summary>
	public static ReadOnlySpan<LadderSpan> All => CollectionsMarshal.AsSpan(Spans);

	public static int Count => Spans.Count;

	public static Ladder NodeAt(int index) => Nodes[index];

	/// <summary>This ladder, in the world.</summary>
	public LadderSpan Span { get; private set; }

	public override void _EnterTree()
	{
		AddToGroup(Group);
		Span = new LadderSpan(GlobalPosition, GlobalBasis.Z, Width, Height);
		Spans.Add(Span);
		Nodes.Add(this);
	}

	public override void _ExitTree()
	{
		int index = Nodes.IndexOf(this);
		if (index >= 0)
		{
			Nodes.RemoveAt(index);
			Spans.RemoveAt(index);
		}
	}

	public override void _Ready()
	{
		if (Solid)
		{
			AddChild(BuildBody());
		}

		AddChild(BuildLink());

		if (!Bootstrap.IsDedicatedServer)
		{
			AddChild(BuildMesh());
		}
	}

	/// <summary>Behind the face, the rungs' width and the stiles', from the foot up to the landing and no higher.</summary>
	private StaticBody3D BuildBody()
	{
		var body = new StaticBody3D
		{
			Name = "Rungs",
			CollisionLayer = CollisionLayers.World,
			CollisionMask = 0u,
		};

		body.AddChild(new CollisionShape3D
		{
			Shape = new BoxShape3D { Size = new Vector3(Width + (2f * StileSide), Height, BodyDepth) },
			Position = new Vector3(0f, Height * 0.5f, -BodyDepth * 0.5f),
		});

		// The navigation bake reads colliders in group `navmesh` and their children
		// (UnitManager.ConfigureBake): a ladder under such a node is read with it, and one that
		// is not is put in the group itself, so the mesh goes round it either way.
		if (!UnderNavigationSource())
		{
			body.AddToGroup(Rts.Structure.NavigationSourceGroup);
		}

		return body;
	}

	private bool UnderNavigationSource()
	{
		for (Node node = GetParent(); node != null; node = node.GetParent())
		{
			if (node.IsInGroup(Rts.Structure.NavigationSourceGroup))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Both ways, from in front of the foot to over the landing, on the ladder layer alone.</summary>
	private NavigationLink3D BuildLink() => new()
	{
		Name = "NavigationLink",
		Bidirectional = true,
		NavigationLayers = NavigationLayer,
		StartPosition = new Vector3(0f, 0f, LinkOutMeters),
		EndPosition = new Vector3(0f, Height, -LinkOverMeters),

		// A climb is slower than a walk: 3 m/s against 5.
		TravelCost = 1.6f,
	};

	/// <summary>Two stiles and a rung every 30 cm, in one mesh; no collision of its own.</summary>
	private MeshInstance3D BuildMesh()
	{
		var tool = new SurfaceTool();
		tool.Begin(Mesh.PrimitiveType.Triangles);

		float stileHeight = Height + StilesAboveMeters;
		float stileX = (Width * 0.5f) + (StileSide * 0.5f);
		var stile = new BoxMesh { Size = new Vector3(StileSide, stileHeight, StileSide) };
		foreach (float x in new[] { -stileX, stileX })
		{
			tool.AppendFrom(stile, 0, new Transform3D(Basis.Identity,
				new Vector3(x, stileHeight * 0.5f, StileSide * 0.5f)));
		}

		var rung = new BoxMesh { Size = new Vector3(Width, RungThickness, RungThickness) };
		for (float y = RungSpacing; y < Height + (RungSpacing * 0.5f); y += RungSpacing)
		{
			tool.AppendFrom(rung, 0, new Transform3D(Basis.Identity, new Vector3(0f, y, StileSide * 0.5f)));
		}

		return new MeshInstance3D
		{
			Name = "Drawn",
			Mesh = tool.Commit(),
			MaterialOverride = new StandardMaterial3D { AlbedoColor = Paint, Roughness = 0.6f, Metallic = 0.2f },
		};
	}
}
