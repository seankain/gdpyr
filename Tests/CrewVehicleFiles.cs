using System;
using System.Collections.Generic;
using System.Globalization;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Tests;

/// <summary>
/// The crew vehicles as their resource files write them (docs/GROUND_VEHICLES.md §5.8),
/// read as text — the engine is the only thing that loads a <c>.tres</c> — and turned
/// into the rules' structs through the same <see cref="CrewLayout"/> builder the
/// engine's <c>CrewVehicleDefinition</c> uses. So the facts about seats and hulls are
/// facts about the files that ship, not about a copy of them in a test.
/// </summary>
internal static class CrewVehicleFiles
{
	public const string FourByFourPath = "Vehicles/four_by_four.tres";

	public const string HelicopterPath = "Vehicles/helicopter.tres";

	public static readonly string[] Paths = { FourByFourPath, HelicopterPath };

	public static Dictionary<string, string> Resource(string path) => TrainedUnitResourceTests.Resource(path);

	public static CrewSeat[] Seats(string path)
	{
		Dictionary<string, string> file = Resource(path);
		return CrewLayout.Seats(
			Ints(file, "SeatRoles"),
			Ints(file, "SeatEnclosed"),
			Vectors(file, "SeatCapsuleBottoms"),
			Vectors(file, "SeatCapsuleTops"),
			Floats(file, "SeatCapsuleRadii"),
			Vectors(file, "SeatEyes"),
			Vectors(file, "SeatDoors"),
			Vectors(file, "SeatExits"),
			Vectors2(file, "SeatArcYawDegrees"),
			Vectors2(file, "SeatArcPitchDegrees"));
	}

	public static HullBox[] Hull(string path)
	{
		Dictionary<string, string> file = Resource(path);
		return CrewLayout.Hull(Vectors(file, "HullCentres"), Vectors(file, "HullHalfExtents"));
	}

	/// <summary>The 4x4's drive traits, as <c>CrewVehicleDefinition.ToFourByFourTraits</c> builds them.</summary>
	public static FourByFourTraits FourByFourTraits()
	{
		Dictionary<string, string> f = Resource(FourByFourPath);
		return new FourByFourTraits(
			F(f, "WheelbaseMeters"), F(f, "HalfTrackMeters"), F(f, "WheelRadiusMeters"), F(f, "SpringRestMeters"),
			F(f, "SpringTravelMeters"), F(f, "SpringFrequencyHz"), F(f, "DampingRatio"),
			F(f, "CentreOfMassHeightMeters"), F(f, "PitchGyrationMeters"), F(f, "RollGyrationMeters"),
			F(f, "MaxSpeed"), F(f, "ReverseSpeed"), F(f, "DriveAcceleration"), F(f, "BrakeDeceleration"),
			F(f, "CoastDeceleration"), F(f, "HandbrakeDeceleration"), F(f, "HandbrakeGripScale"),
			F(f, "GripAcceleration"), F(f, "SteerRate"), Mathf.DegToRad(F(f, "SteerLimitAtRestDegrees")),
			Mathf.DegToRad(F(f, "SteerLimitAtTopDegrees")), F(f, "AirborneRateDamping"));
	}

	/// <summary>The helicopter's flight traits, as <c>CrewVehicleDefinition.ToHelicopterTraits</c> builds them.</summary>
	public static HelicopterTraits HelicopterTraits()
	{
		Dictionary<string, string> f = Resource(HelicopterPath);
		return new HelicopterTraits(
			F(f, "SpinUpSeconds"), F(f, "SpinDownSeconds"), F(f, "LiftRotor"), F(f, "MaxForwardSpeed"),
			F(f, "MaxSideSpeed"), F(f, "FlightAcceleration"), F(f, "ClimbSpeed"), F(f, "DescentSpeed"),
			F(f, "VerticalAcceleration"), Mathf.DegToRad(F(f, "TurnRateDegrees")), F(f, "CeilingMeters"),
			F(f, "UnpilotedDescentSpeed"), F(f, "UnpilotedDrag"), Mathf.DegToRad(F(f, "MaxTiltDegrees")),
			F(f, "TiltResponse"));
	}

	public static float F(Dictionary<string, string> file, string key) => TrainedUnitResourceTests.Float(file, key);

	public static int[] Ints(Dictionary<string, string> file, string key) =>
		Array.ConvertAll(Numbers(file, key, "PackedInt32Array"), v => (int)v);

	public static float[] Floats(Dictionary<string, string> file, string key) =>
		Numbers(file, key, "PackedFloat32Array");

	public static Vector3[] Vectors(Dictionary<string, string> file, string key)
	{
		float[] n = Numbers(file, key, "PackedVector3Array");
		if (n.Length % 3 != 0)
		{
			throw new FormatException($"{key}: {n.Length} numbers is not a whole number of Vector3s");
		}

		var vectors = new Vector3[n.Length / 3];
		for (int i = 0; i < vectors.Length; i++)
		{
			vectors[i] = new Vector3(n[3 * i], n[(3 * i) + 1], n[(3 * i) + 2]);
		}

		return vectors;
	}

	public static Vector2[] Vectors2(Dictionary<string, string> file, string key)
	{
		float[] n = Numbers(file, key, "PackedVector2Array");
		if (n.Length % 2 != 0)
		{
			throw new FormatException($"{key}: {n.Length} numbers is not a whole number of Vector2s");
		}

		var vectors = new Vector2[n.Length / 2];
		for (int i = 0; i < vectors.Length; i++)
		{
			vectors[i] = new Vector2(n[2 * i], n[(2 * i) + 1]);
		}

		return vectors;
	}

	/// <summary>A <c>Vector3(x, y, z)</c> or <c>Vector2(x, y)</c> value.</summary>
	public static float[] Tuple(Dictionary<string, string> file, string key, string type) => Numbers(file, key, type);

	/// <summary>The numbers inside <c>Type(…)</c>, invariant culture.</summary>
	private static float[] Numbers(Dictionary<string, string> file, string key, string type)
	{
		string value = file[key];
		if (!value.StartsWith(type + "(", StringComparison.Ordinal) || !value.EndsWith(')'))
		{
			throw new FormatException($"{key} = {value} is not a {type}");
		}

		string inner = value[(type.Length + 1)..^1].Trim();
		if (inner.Length == 0)
		{
			return Array.Empty<float>();
		}

		string[] parts = inner.Split(',');
		var numbers = new float[parts.Length];
		for (int i = 0; i < parts.Length; i++)
		{
			numbers[i] = float.Parse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
		}

		return numbers;
	}
}
