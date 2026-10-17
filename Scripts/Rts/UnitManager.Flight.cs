using Gdpyr.Core;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// The dropship's flying (docs/VEHICLES.md §8.2): the half of <see cref="Flight"/> that
/// needs the physics world. Every tick an aircraft is told where its order wants it,
/// bounded to the field; it takes off when that is further than its arrival radius,
/// measured flat, and otherwise comes down — over a landing spot it found clear first,
/// or, finding none, holding cruise with <see cref="Unit.CannotLand"/> set and looking
/// again a second later. Below two metres it looks once more for bodies under it and
/// waits there until they have gone.
///
/// The ray and the shape queries share <see cref="WorldRay"/>'s cached ray with the exit
/// slots, and keep one hull cylinder per unit kind, changed in place.
/// </summary>
public partial class UnitManager
{
	/// <summary>How close, flat, over its landing spot an aircraft has to be before it comes down.</summary>
	private const float LandingToleranceMeters = 0.5f;

	/// <summary>How far above its feet the ray for an aircraft's height starts, so a body resting on the floor does not start inside it.</summary>
	private const float HeightProbeLiftMeters = 0.25f;

	/// <summary>How far past its cruise altitude below it the ground under an aircraft is looked for.</summary>
	private const float HeightProbeSlackMeters = 30f;

	/// <summary>How far down from above a landing candidate the surface it would come down on is looked for.</summary>
	private const float LandingProbeMeters = FlightTraits.MaxCruiseAltitudeMeters + HeightProbeSlackMeters;

	/// <summary>
	/// How far into a landing surface the ray that asks what is under it starts. Inside a
	/// solid box — the floor, a barracks, a cover block — that box is not hit, so anything
	/// the ray does find is a second surface under the first.
	/// </summary>
	private const float SurfaceDepthMeters = 0.6f;

	/// <summary>How far under a landing surface something solid makes it a roof, a platform or a block rather than the ground.</summary>
	private const float ElevatedProbeMeters = 30f;

	/// <summary>How long an aircraft that found nowhere to land waits before it looks again: a second.</summary>
	private const int LandingRetryTicks = SimConfig.TickRate;

	/// <summary>One hull cylinder per unit kind, made once and changed in place; one shape query and its exclusions.</summary>
	private readonly CylinderShape3D[] _hullShapes = new CylinderShape3D[SimConfig.MaxUnitDefinitions];
	private PhysicsShapeQueryParameters3D _hullQuery;
	private readonly Godot.Collections.Array<Rid> _hullExclude = new();

	/// <summary>
	/// Where an aircraft's order sends it, bounded where it steers rather than where orders
	/// arrive: a fresh unit's move from a rally point nobody checked, a placed unit's order
	/// and a client's order all reach it, and none of them is checked against the field.
	/// A point that is not finite is where it already is.
	/// </summary>
	private static Vector3 AircraftDestination(Unit unit, Vector3 destination)
	{
		if (!destination.IsFinite())
		{
			return unit.GlobalPosition;
		}

		destination.X = Mathf.Clamp(destination.X, -Cargo.FieldHalfExtentMeters, Cargo.FieldHalfExtentMeters);
		destination.Z = Mathf.Clamp(destination.Z, -Cargo.FieldHalfExtentMeters, Cargo.FieldHalfExtentMeters);
		return destination;
	}

	/// <summary>
	/// How far, flat, an aircraft is from where its order sends it: a dropship fourteen
	/// metres over its destination is there. Nought once it has settled for a landing spot
	/// near that destination, so a spot twelve metres off is not a reason to take off again.
	/// </summary>
	private static float AircraftDistance(Unit unit, Vector3 destination) =>
		IsSettledFor(unit, destination) ? 0f : Flat(unit.GlobalPosition, destination);

	/// <summary>Whether the landing spot an aircraft has is the one it found for <paramref name="destination"/>.</summary>
	private static bool IsSettledFor(Unit unit, Vector3 destination) =>
		unit.HasLandingSpot && unit.LandingFor.DistanceSquaredTo(destination) < 0.01f;

	/// <summary>
	/// One tick of an aircraft's flight, in place of a ground unit's step: its height
	/// measured, a landing spot found if it is coming down and has none, the ground under it
	/// checked on the last two metres, the phase machine stepped, and the body moved.
	/// <paramref name="destination"/> is already bounded (<see cref="AircraftDestination"/>).
	/// </summary>
	private void SteerAircraft(Unit unit, Vector3 destination, bool hasDestination, uint tick)
	{
		Vector3 at = unit.GlobalPosition;
		bool wantsAir = hasDestination && AircraftDistance(unit, destination) > unit.Traits.ArrivalRadiusMeters;
		if (wantsAir)
		{
			unit.HasLandingSpot = false;
			unit.CannotLand = false;
			unit.LandingRetryTick = 0;
		}

		FlightPhase before = unit.Phase;
		unit.HeightAboveGround = before == FlightPhase.Grounded ? 0f : MeasureHeight(unit);

		// Nowhere to go and still up: somewhere to come down, found before it starts down.
		Vector3 flyTo = destination;
		if (!wantsAir && before is FlightPhase.TakingOff or FlightPhase.Cruising)
		{
			if (!unit.HasLandingSpot && tick >= unit.LandingRetryTick)
			{
				Vector3 centre = hasDestination ? destination : at;
				if (TryLandingSpot(unit, centre, out Vector3 spot))
				{
					unit.HasLandingSpot = true;
					unit.LandingSpot = spot;
					unit.LandingFor = centre;
					unit.CannotLand = false;
				}
				else
				{
					unit.CannotLand = true;
					unit.LandingRetryTick = tick + LandingRetryTicks;
				}
			}

			if (unit.HasLandingSpot && Flat(at, unit.LandingSpot) > LandingToleranceMeters)
			{
				wantsAir = true;
				flyTo = unit.LandingSpot;
			}
		}

		unit.Flight.GroundBlocked = Flight.NeedsGroundCheck(unit.Flight, unit.HeightAboveGround) && IsGroundBlocked(unit);

		float vertical = Flight.Step(unit.FlightTraits, ref unit.Flight, wantsAir, unit.HeightAboveGround,
			SimConfig.TickDelta, out bool horizontal);

		// Solid or not before it moves: off its layer from the tick it lifts, on it from the
		// tick it touches down.
		bool changed = unit.Phase != before;
		if (changed)
		{
			unit.ApplyPresentation();
		}

		unit.SteerFlying(flyTo, horizontal, vertical, SimConfig.TickDelta);

		if (changed && unit.Phase == FlightPhase.Grounded)
		{
			Landed(unit, tick);
		}
	}

	/// <summary>
	/// An aircraft has touched down: on the ground, nothing under it to wait for, and a
	/// <c>dropship_landed</c> on the agent event stream with how many were aboard. It keeps
	/// its landing spot, so an order to a place it could not land at does not lift it again.
	/// </summary>
	private void Landed(Unit unit, uint tick)
	{
		unit.HeightAboveGround = 0f;
		unit.CannotLand = false;
		unit.Flight.GroundBlocked = false;

		Vector3 at = unit.GlobalPosition;
		AgentEventBus.Emit(AgentEventKind.DropshipLanded, tick, unit.UnitId, unit.AboardCount, 0, at.X, at.Z);
	}

	/// <summary>
	/// How far an aircraft's feet are above the ground under it: one world-layer ray down.
	/// Not a number when there is no ground under it to find — which the phase machine
	/// treats as cruise, neither climbing nor coming down.
	/// </summary>
	private float MeasureHeight(Unit unit)
	{
		PhysicsDirectSpaceState3D space = GetTree()?.Root?.World3D?.DirectSpaceState;
		if (space == null)
		{
			return float.NaN;
		}

		Vector3 feet = unit.GlobalPosition;
		float probe = unit.FlightTraits.CruiseAltitudeMeters + HeightProbeSlackMeters;
		return WorldRay(space, feet + (Vector3.Up * HeightProbeLiftMeters), feet - (Vector3.Up * probe), out Vector3 ground)
			? Mathf.Max(feet.Y - ground.Y, 0f)
			: float.NaN;
	}

	/// <summary>
	/// The first of <see cref="Flight.LandingCandidate"/>'s places round
	/// <paramref name="centre"/> an aircraft can come down at, and where on the ground that
	/// is. With no physics world to ask, the centre.
	/// </summary>
	private bool TryLandingSpot(Unit unit, Vector3 centre, out Vector3 spot)
	{
		spot = centre;
		PhysicsDirectSpaceState3D space = GetTree()?.Root?.World3D?.DirectSpaceState;
		if (space == null)
		{
			return true;
		}

		for (int ordinal = 0; ordinal < Flight.LandingCandidateCount; ordinal++)
		{
			Vector3 candidate = Flight.LandingCandidate(centre, unit.GlobalPosition, ordinal);
			if (Cargo.InField(candidate) && IsLandingSite(space, unit, candidate, out spot))
			{
				return true;
			}
		}

		spot = centre;
		return false;
	}

	/// <summary>
	/// §5.4's checks of an exit slot, bar reachability, sized for a hull coming straight
	/// down (docs/VEHICLES.md §8.2):
	/// <list type="bullet">
	/// <item>ground — the first surface under the candidate from above, which is what a
	/// hull coming straight down meets; and nothing solid under that surface, so it is the
	/// ground and not a barracks roof, a platform or a cover block, whose tops the
	/// navigation mesh is baked over too;</item>
	/// <item>navigation mesh — within the hull's own radius of it;</item>
	/// <item>clearance — a cylinder the hull's width from the ground to clear altitude,
	/// against the world, units and players, leaving out the aircraft and the units
	/// walking to get into it.</item>
	/// </list>
	/// </summary>
	private bool IsLandingSite(PhysicsDirectSpaceState3D space, Unit unit, Vector3 candidate, out Vector3 ground)
	{
		FlightTraits traits = unit.FlightTraits;
		float top = Mathf.Max(unit.GlobalPosition.Y, candidate.Y + traits.ClearAltitudeMeters) + 1f;
		var above = new Vector3(candidate.X, top, candidate.Z);
		if (!WorldRay(space, above, above - (Vector3.Up * LandingProbeMeters), out ground))
		{
			return false;
		}

		if (WorldRay(space, ground - (Vector3.Up * SurfaceDepthMeters), ground - (Vector3.Up * ElevatedProbeMeters), out _))
		{
			return false;
		}

		float hull = RadiusOf(unit);
		if (NavigationReady)
		{
			Vector3 walkable = NavigationServer3D.MapGetClosestPoint(GetTree().Root.World3D.NavigationMap, ground);
			if (Flat(walkable, ground) > hull + ExitNavigationSlackMeters
				|| Mathf.Abs(walkable.Y - ground.Y) > ExitNavigationClimbMeters)
			{
				return false;
			}
		}

		return !IsHullObstructed(space, unit, ground, traits.ClearAltitudeMeters,
			CollisionLayers.World | CollisionLayers.Units | CollisionLayers.Players);
	}

	/// <summary>
	/// Whether a unit or a player is under an aircraft coming down: the hull's width from
	/// the ground up to its feet, leaving out the units walking to get into it, who stand
	/// off round it rather than under it.
	/// </summary>
	private bool IsGroundBlocked(Unit unit)
	{
		PhysicsDirectSpaceState3D space = GetTree()?.Root?.World3D?.DirectSpaceState;
		if (space == null || !float.IsFinite(unit.HeightAboveGround))
		{
			return false;
		}

		Vector3 ground = unit.GlobalPosition - (Vector3.Up * unit.HeightAboveGround);
		return IsHullObstructed(space, unit, ground, unit.HeightAboveGround, CollisionLayers.Units | CollisionLayers.Players);
	}

	/// <summary>
	/// Whether anything on <paramref name="mask"/> is inside a cylinder the aircraft's width
	/// standing on <paramref name="ground"/>, from just off it to
	/// <paramref name="height"/> above it — the aircraft itself and its boarders aside.
	/// </summary>
	private bool IsHullObstructed(PhysicsDirectSpaceState3D space, Unit unit, Vector3 ground, float height, uint mask)
	{
		float span = height - PlacementProbeLiftMeters;
		if (span <= 0.05f)
		{
			return false;
		}

		byte kind = unit.DefinitionId;
		float radius = RadiusOf(unit);
		CylinderShape3D cylinder = kind < _hullShapes.Length
			? (_hullShapes[kind] ??= new CylinderShape3D { Radius = radius, Height = span })
			: new CylinderShape3D { Radius = radius, Height = span };
		cylinder.Height = span;

		_hullQuery ??= new PhysicsShapeQueryParameters3D();
		_hullQuery.Shape = cylinder;
		_hullQuery.CollisionMask = mask;
		_hullQuery.Transform = new Transform3D(Basis.Identity,
			ground + (Vector3.Up * (PlacementProbeLiftMeters + (span * 0.5f))));

		_hullExclude.Clear();
		_hullExclude.Add(unit.GetRid());
		int owner = OwnerId.ForUnit(unit.UnitId);
		for (int i = 0; i < _ordered.Count; i++)
		{
			if (IsBoarding(_ordered[i], owner))
			{
				_hullExclude.Add(_ordered[i].GetRid());
			}
		}

		_hullQuery.Exclude = _hullExclude;
		return space.IntersectShape(_hullQuery, 1).Count > 0;
	}

	/// <summary>
	/// Where a unit a barracks turned out appears: its slot in front of the door, or, for
	/// an aircraft, that pushed out along the door's normal by its radius and a metre — the
	/// slot is two metres from the wall, inside a hull of two.
	/// </summary>
	private static Vector3 SpawnPoint(Barracks barracks, byte definitionId)
	{
		Vector3 slot = barracks.SpawnSlot(barracks.Queue.Produced);
		if (!UnitCatalog.IsAircraft(definitionId))
		{
			return slot;
		}

		float radius = UnitCatalog.Definition(definitionId)?.RadiusMeters ?? 0f;
		return slot + (barracks.Forward * (radius + 1f));
	}
}
