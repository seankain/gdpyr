using System;

namespace Gdpyr.Sim.Level;

/// <summary>
/// What the ground wears, in the order it is asked for (docs/LEVEL_EDITOR.md §1):
/// water, then sand, mud, grass and stone as the ground gets higher or steeper.
/// </summary>
public enum TerrainBand : byte
{
	Water = 0,
	Sand = 1,
	Mud = 2,
	Grass = 3,
	Stone = 4,
}

/// <summary>
/// A map's water level and its six band numbers (§6.4, §6.8). The defaults are the
/// plan's; a map carries its own.
/// </summary>
public struct BandSettings
{
	/// <summary>The largest a band number may be: what a <c>u16</c> of thousandths holds (§6.8).</summary>
	public const float MaxNumber = 65.535f;

	/// <summary>The water's height [m]. Ground under it is water.</summary>
	public float WaterLevelMeters;

	/// <summary>The slope that costs nothing [degrees].</summary>
	public float SlopeFreeDegrees;

	/// <summary>What a degree of slope past that is worth, in metres of height.</summary>
	public float SlopeGainMetersPerDegree;

	/// <summary>Where sand gives way to mud [m of u].</summary>
	public float SandTopMeters;

	/// <summary>Where mud gives way to grass [m of u].</summary>
	public float MudTopMeters;

	/// <summary>Where grass gives way to stone [m of u].</summary>
	public float GrassTopMeters;

	/// <summary>How much of u two neighbouring bands are mixed across where they meet [m]. For drawing only.</summary>
	public float BlendMeters;

	/// <summary>§6.4's numbers, with the water at 0 m.</summary>
	public static BandSettings Default => new()
	{
		WaterLevelMeters = 0f,
		SlopeFreeDegrees = 15f,
		SlopeGainMetersPerDegree = 0.6f,
		SandTopMeters = 1.5f,
		MudTopMeters = 4f,
		GrassTopMeters = 22f,
		BlendMeters = 1f,
	};

	/// <summary>
	/// Whether these are numbers a file can hold and a map can mean: the water inside
	/// LE-D4's limits, each of the six from 0 to <see cref="MaxNumber"/>, and the
	/// three edges in their order.
	/// </summary>
	public readonly bool IsValid =>
		WaterLevelMeters >= MapLimits.MinHeightMeters && WaterLevelMeters <= MapLimits.MaxHeightMeters
		&& InRange(SlopeFreeDegrees) && InRange(SlopeGainMetersPerDegree)
		&& InRange(SandTopMeters) && InRange(MudTopMeters) && InRange(GrassTopMeters) && InRange(BlendMeters)
		&& SandTopMeters <= MudTopMeters && MudTopMeters <= GrassTopMeters;

	private static bool InRange(float number) => number >= 0f && number <= MaxNumber;
}

/// <summary>
/// A band, and what to draw: the two neighbouring bands that meet nearest and how
/// much of the upper one. Away from where two meet, both are the band.
/// </summary>
public readonly struct BandSample
{
	/// <summary>The band the ground is in.</summary>
	public readonly TerrainBand Band;

	/// <summary>The earlier of the two bands to draw.</summary>
	public readonly TerrainBand Lower;

	/// <summary>The later of the two.</summary>
	public readonly TerrainBand Upper;

	/// <summary>How much of <see cref="Upper"/>, of 1. The rest is <see cref="Lower"/>.</summary>
	public readonly float UpperWeight;

	public BandSample(TerrainBand band, TerrainBand lower, TerrainBand upper, float upperWeight)
	{
		Band = band;
		Lower = lower;
		Upper = upper;
		UpperWeight = upperWeight;
	}

	public float LowerWeight => 1f - UpperWeight;

	/// <summary>How much of a band to draw here: 0 for any but the two.</summary>
	public float WeightOf(TerrainBand band) =>
		(band == Upper ? UpperWeight : 0f) + (band == Lower ? LowerWeight : 0f);
}

/// <summary>
/// The terrain's texture, computed and never painted (docs/LEVEL_EDITOR.md LE-D7,
/// §6.4). With e the height above the map's water and s the slope in degrees:
///
/// <code>
///   under the water (e &lt; 0):  water
///   otherwise  u = e + 0.6 · max(0, s − 15)
///              sand below u = 1.5,  mud below 4,  grass below 22,  stone above
/// </code>
///
/// u does not fall when e or s rises, and a band is how many edges u has passed, so
/// higher or steeper never gives an earlier band. Nothing is stored and nothing goes
/// stale when the ground is edited. The terrain's shader computes the same u from a
/// fragment's height and normal; this is what the editor's overlay, a map's preview
/// and the tests use, and the two need not agree to the bit, since nothing but the
/// picture reads the shader.
/// </summary>
public static class TerrainBands
{
	public const int Count = 5;

	/// <summary>The bands over the water, in the order u passes into them.</summary>
	private static readonly TerrainBand[] Land = { TerrainBand.Sand, TerrainBand.Mud, TerrainBand.Grass, TerrainBand.Stone };

	/// <summary>
	/// §6.4's u [m]: the height above the water, and 0.6 m for each degree of slope
	/// past 15°. Meaningful for ground that is not under the water.
	/// </summary>
	public static float U(in BandSettings settings, float heightMeters, float slopeDegrees) =>
		(heightMeters - settings.WaterLevelMeters)
		+ (settings.SlopeGainMetersPerDegree * MathF.Max(0f, slopeDegrees - settings.SlopeFreeDegrees));

	/// <summary>The band of ground at a height and a slope.</summary>
	public static TerrainBand BandOf(in BandSettings settings, float heightMeters, float slopeDegrees)
	{
		if (heightMeters < settings.WaterLevelMeters)
		{
			return TerrainBand.Water;
		}

		return Land[Passed(settings, U(settings, heightMeters, slopeDegrees))];
	}

	/// <summary>
	/// The band and, for drawing, the two bands that meet nearest with a weight for
	/// each: across <see cref="BandSettings.BlendMeters"/> of u centred on their edge
	/// the upper one's weight runs from 0 to 1, and it is ½ on the edge. The water's
	/// edge is not blended: the water's own surface is what is drawn there.
	/// </summary>
	public static BandSample Classify(in BandSettings settings, float heightMeters, float slopeDegrees)
	{
		if (heightMeters < settings.WaterLevelMeters)
		{
			return new BandSample(TerrainBand.Water, TerrainBand.Water, TerrainBand.Water, 0f);
		}

		float u = U(settings, heightMeters, slopeDegrees);
		TerrainBand band = Land[Passed(settings, u)];

		// The nearest edge, if u is within half the blend of one.
		float half = settings.BlendMeters * 0.5f;
		float nearest = float.MaxValue;
		float edge = 0f;
		for (int i = 0; i < 3; i++)
		{
			float candidate = Edge(settings, i);
			float away = MathF.Abs(u - candidate);
			if (away < half && away < nearest)
			{
				nearest = away;
				edge = candidate;
			}
		}

		if (nearest == float.MaxValue)
		{
			return new BandSample(band, band, band, 0f);
		}

		// The bands either side of that edge: with two edges on one number, the band between them is not there to draw.
		TerrainBand lower = Land[Passed(settings, edge) - AtEdge(settings, edge)];
		TerrainBand upper = Land[Passed(settings, edge)];
		return new BandSample(band, lower, upper, Math.Clamp(((u - edge) / settings.BlendMeters) + 0.5f, 0f, 1f));
	}

	/// <summary>The band of the ground at a sample: its height, and its slope by central differences (§6.1).</summary>
	public static BandSample Classify(in BandSettings settings, HeightField field, int x, int z) =>
		Classify(settings, field[x, z], field.SlopeDegrees(x, z));

	private static float Edge(in BandSettings settings, int index) => index switch
	{
		0 => settings.SandTopMeters,
		1 => settings.MudTopMeters,
		_ => settings.GrassTopMeters,
	};

	/// <summary>How many edges u has passed: 0 on sand, 3 on stone.</summary>
	private static int Passed(in BandSettings settings, float u) =>
		(u >= settings.SandTopMeters ? 1 : 0) + (u >= settings.MudTopMeters ? 1 : 0) + (u >= settings.GrassTopMeters ? 1 : 0);

	/// <summary>How many edges are exactly at a value.</summary>
	private static int AtEdge(in BandSettings settings, float value) =>
		(value == settings.SandTopMeters ? 1 : 0) + (value == settings.MudTopMeters ? 1 : 0) + (value == settings.GrassTopMeters ? 1 : 0);
}
