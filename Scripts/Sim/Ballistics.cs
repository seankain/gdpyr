using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// One projectile type's physical constants, in SI units.
///
/// Ported from <c>pyrrhic/Assets/Scripts/NPC/Weapons/BallisticArc.cs</c>
/// (<c>BulletData</c>), itself from Erik Nordeus' Unity ballistics tutorial. The
/// port is a <c>UnityEngine.Vector3</c> → <c>Godot.Vector3</c> substitution, a
/// move from a mutable class to a readonly struct — this is read once per
/// integration sub-step and must not allocate — and SI units throughout, with the
/// grams/millimetres a weapon designer thinks in pushed into a factory.
///
/// <see cref="DragCoefficient"/> is deliberately *not* called a G1 ballistic
/// coefficient. The reference passed a G1 BC straight into the drag term; the two
/// are different quantities (a G1 BC is a mass-to-form-factor ratio referenced to
/// a standard projectile, not the dimensionless C_d of the drag equation), so
/// doing that silently mis-scales drag. Here it is what the equation actually
/// wants, and — per docs/NETCODE.md §4.4 — it is a tuning knob, not a realism
/// constant.
/// </summary>
public readonly struct ProjectileParams
{
	/// <summary>Projectile mass [kg]. Floored above zero: it is a divisor.</summary>
	public readonly float MassKilograms;

	/// <summary>Projectile radius [m], which sets the cross-section the air sees.</summary>
	public readonly float RadiusMeters;

	/// <summary>Dimensionless C_d in F_drag = ½ ρ v² C_d A.</summary>
	public readonly float DragCoefficient;

	/// <summary>Dimensionless C_l. Zero unless a weapon wants a deliberately curving round.</summary>
	public readonly float LiftCoefficient;

	/// <summary>Density of the medium [kg/m³].</summary>
	public readonly float AirDensity;

	/// <summary>Wind velocity [m/s]. Drag acts on velocity *relative to* this.</summary>
	public readonly Vector3 WindVelocity;

	public ProjectileParams(float massKilograms, float radiusMeters, float dragCoefficient,
		float liftCoefficient = 0f, float airDensity = SimConfig.AirDensity, Vector3 windVelocity = default)
	{
		MassKilograms = MathF.Max(massKilograms, 1e-6f);
		RadiusMeters = MathF.Max(radiusMeters, 0f);
		DragCoefficient = MathF.Max(dragCoefficient, 0f);
		LiftCoefficient = liftCoefficient;
		AirDensity = MathF.Max(airDensity, 0f);
		WindVelocity = windVelocity;
	}

	/// <summary>Cross-section area [m²], also used as the lift planform area.</summary>
	public float CrossSectionArea => MathF.PI * RadiusMeters * RadiusMeters;

	/// <summary>True when no medium acts on the projectile, so the vacuum solution holds exactly.</summary>
	public bool IsVacuum => AirDensity <= 0f || (DragCoefficient <= 0f && LiftCoefficient == 0f) || RadiusMeters <= 0f;

	/// <summary>The units a weapon definition is authored in.</summary>
	public static ProjectileParams FromGramsAndMillimeters(float massGrams, float radiusMillimeters,
		float dragCoefficient, float liftCoefficient = 0f, float airDensity = SimConfig.AirDensity,
		Vector3 windVelocity = default) =>
		new(massGrams / 1000f, radiusMillimeters / 1000f, dragCoefficient, liftCoefficient, airDensity, windVelocity);
}

/// <summary>
/// The projectile integrator (docs/NETCODE.md §4.1). Pure math over
/// <see cref="Vector3"/>, no engine, no allocation, no state — which is what lets
/// every peer reproduce an identical trajectory from a spawn record alone
/// (docs/NETCODE.md §4.2) and what makes it testable without Godot.
///
/// Ported from <c>IntegrationMethods.Heuns</c> and <c>BulletPhysics</c> in
/// pyrrhic. The <c>BallisticArc</c> class around them is deliberately *not*
/// ported: it precomputed a <c>List&lt;BallisticArcPoint&gt;</c> per shot, which
/// allocates on every trigger pull. The integrator is stepped live instead.
/// </summary>
public static class Ballistics
{
	/// <summary>
	/// Projectile gravity [m/s²]. Held here rather than read from the project's
	/// <c>default_gravity</c> on purpose: every peer must integrate with the same
	/// constant, and the character controller's gravity is a *feel* setting that
	/// will be tuned independently of ballistics.
	/// </summary>
	public const float Gravity = 9.81f;

	public static readonly Vector3 GravityVector = new(0f, -Gravity, 0f);

	/// <summary>
	/// Drag acceleration [m/s²], opposing motion through the air:
	/// F = ½ ρ v² C_d A, then a = F / m.
	/// </summary>
	public static Vector3 DragAcceleration(Vector3 velocity, in ProjectileParams p)
	{
		Vector3 relative = velocity - p.WindVelocity;
		float speed = relative.Length();
		if (speed <= 0f)
		{
			return Vector3.Zero;
		}

		float force = 0.5f * p.AirDensity * speed * speed * p.DragCoefficient * p.CrossSectionArea;
		return relative / speed * -(force / p.MassKilograms);
	}

	/// <summary>
	/// Lift acceleration [m/s²] along <paramref name="up"/>, which is expected to
	/// be perpendicular to the direction of travel. Zero for every weapon in the
	/// greybox; kept because dropping it would make the port silently different
	/// from its source.
	/// </summary>
	public static Vector3 LiftAcceleration(Vector3 velocity, in ProjectileParams p, Vector3 up)
	{
		if (p.LiftCoefficient == 0f || up == Vector3.Zero)
		{
			return Vector3.Zero;
		}

		Vector3 relative = velocity - p.WindVelocity;
		float speed = relative.Length();
		float force = 0.5f * p.AirDensity * speed * speed * p.LiftCoefficient * p.CrossSectionArea;
		return up * (force / p.MassKilograms);
	}

	/// <summary>Total acceleration at a given velocity: gravity plus the medium.</summary>
	public static Vector3 Acceleration(Vector3 velocity, in ProjectileParams p, Vector3 up) =>
		GravityVector + DragAcceleration(velocity, p) + LiftAcceleration(velocity, p, up);

	/// <summary>
	/// One Heun (improved Euler) step. Second order, and — unlike forward Euler —
	/// it re-evaluates the acceleration at the predicted end of the step, which
	/// matters because drag depends on velocity and velocity is exactly what the
	/// step changes.
	/// </summary>
	public static void Step(float dt, Vector3 position, Vector3 velocity, in ProjectileParams p, Vector3 up,
		out Vector3 newPosition, out Vector3 newVelocity)
	{
		Vector3 accelerationStart = Acceleration(velocity, p, up);

		// Predictor: where forward Euler says the velocity lands.
		Vector3 velocityEuler = velocity + dt * accelerationStart;

		// Corrector: average the slope at both ends of the step.
		Vector3 accelerationEnd = Acceleration(velocityEuler, p, up);

		newVelocity = velocity + dt * 0.5f * (accelerationStart + accelerationEnd);
		newPosition = position + dt * 0.5f * (velocity + velocityEuler);
	}

	/// <summary>
	/// Advances a projectile over <paramref name="dt"/> in
	/// <paramref name="subSteps"/> equal Heun steps.
	///
	/// A 940 m/s round covers 15.7 m in one 60 Hz tick, and a single step that long
	/// integrates drag far too coarsely; sub-stepping is what keeps the trajectory
	/// stable and step-size independent (docs/NETCODE.md §4.5). Hit detection still
	/// uses the tick's chord — over one tick the arc's departure from it is
	/// sub-millimetre.
	/// </summary>
	public static void Integrate(float dt, int subSteps, ref Vector3 position, ref Vector3 velocity,
		in ProjectileParams p)
	{
		int steps = Math.Max(subSteps, 1);
		float h = dt / steps;

		for (int i = 0; i < steps; i++)
		{
			Step(h, position, velocity, p, Vector3.Zero, out Vector3 nextPosition, out Vector3 nextVelocity);
			position = nextPosition;
			velocity = nextVelocity;
		}
	}

	/// <summary>
	/// The closed-form vacuum solution p(t) = p₀ + v₀t + ½gt², which the drag-free
	/// integration is tested against and which docs/NETCODE.md §4.4's lead and drop
	/// table is computed from.
	/// </summary>
	public static Vector3 VacuumPosition(Vector3 origin, Vector3 velocity, float seconds) =>
		origin + velocity * seconds + 0.5f * seconds * seconds * GravityVector;
}
