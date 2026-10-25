using System;
using Gdpyr.Sim.Htn;
using Godot;

namespace Gdpyr.Sim;

/// <summary>
/// A builder at its site that the enemy can reach (docs/TACTICS.md §5.10): where it stands,
/// and what threatens it — where it was last fired upon from while its window is open, else
/// the nearest contact the side remembers near it.
/// </summary>
public struct EscortCharge
{
	public ushort UnitId;

	public Vector3 Position;

	/// <summary>The threat's feet: what an escort's spot is cover from, and what its screen faces.</summary>
	public Vector3 Threat;

	/// <summary>The builder was fired upon within <see cref="FiredUpon.WindowTicks"/>.</summary>
	public bool FiredUpon;

	/// <summary>Where the latest round at the builder came from, as the builder reckons it. Only with <see cref="FiredUpon"/>.</summary>
	public Vector3 FiredFrom;

	/// <summary>The tick of that round.</summary>
	public uint FiredTick;

	/// <summary>Whose that round was, as an <see cref="OwnerId"/>: who its escorts look for first.</summary>
	public int ShooterOwnerId;
}

/// <summary>A unit that may escort a builder (docs/TACTICS.md §5.10): where it is, and the ground it holds.</summary>
public struct EscortCandidate
{
	public ushort UnitId;

	public Vector3 Position;

	/// <summary>Its order's anchor: the point it defends, or where it was told to stop.</summary>
	public Vector3 Anchor;

	public float LeashMeters;

	/// <summary>The builder it escorted on the last pass; 0 for none. It keeps that duty against a unit up to <see cref="EscortPlan.KeepMeters"/> nearer.</summary>
	public ushort Previous;
}

/// <summary>
/// Escorting a builder (docs/TACTICS.md §5.10), as numbers and pure functions: which
/// builders are exposed, which units escort which, and where an escort stands when no
/// wall by the builder will do. No ray: the builder's fire memory, what the side remembers,
/// and plain geometry. The cover-board search for a spot is the engine half's, by the
/// board's own rules; the timing of covering fire is the planner's.
/// </summary>
public static class EscortPlan
{
	/// <summary>How far from its site's base point a builder is still at its site [m]: working, or behind the cover it took from there (a 15 m search).</summary>
	public const float SiteMeters = 20f;

	/// <summary>How near a builder a contact the side remembers makes it exposed, without a round yet [m].</summary>
	public const float ThreatMeters = 30f;

	/// <summary>How many units escort one builder.</summary>
	public const int PerBuilder = 2;

	/// <summary>How much nearer than an escort another unit must be to take its builder from it [m]: no swapping between two that walk past each other.</summary>
	public const float KeepMeters = 3f;

	/// <summary>How far from the builder an escort looks for a low wall to cover it from [m].</summary>
	public const float SpotMeters = 8f;

	/// <summary>How far from the builder, towards the threat, an escort with no such wall stands [m].</summary>
	public const float ScreenMeters = 4f;

	/// <summary>How far apart two escorts screening one builder stand, across the line to the threat [m].</summary>
	public const float ScreenSpacingMeters = 3f;

	/// <summary>A screen stands no nearer the threat than this [m]: the fight's own minimum (<c>CoverMinThreatMeters</c>).</summary>
	public const float ScreenStandoffMeters = 6f;

	/// <summary>How many exposed builders are escorted at once.</summary>
	public const int ChargeCapacity = 16;

	/// <summary>Whether a builder at <paramref name="builder"/> is at the site whose base point is <paramref name="site"/>, measured flat.</summary>
	public static bool AtSite(Vector3 builder, Vector3 site) => Flat(builder, site) <= SiteMeters;

	/// <summary>
	/// What threatens a builder, if anything (docs/TACTICS.md §5.10): where it was fired upon
	/// from while its window is open; else the nearest contact <paramref name="memory"/> still
	/// remembers within <see cref="ThreatMeters"/> of it, live or a ghost, that
	/// <paramref name="alive"/> says has not died since. False for neither: it is not exposed.
	/// </summary>
	public static bool TryThreat(Vector3 builder, bool firedUpon, Vector3 firedFromFeet, ContactMemory memory, uint tick,
		Func<int, bool> alive, out Vector3 threat)
	{
		if (firedUpon)
		{
			threat = firedFromFeet;
			return true;
		}

		threat = Vector3.Zero;
		float best = ThreatMeters;
		bool found = false;
		for (int i = 0; memory != null && i < memory.Count; i++)
		{
			if (!memory.IsRemembered(i, tick) || (alive != null && !alive(memory.At(i).OwnerId)))
			{
				continue;
			}

			Vector3 at = memory.At(i).Position;
			float meters = Flat(builder, at);
			if (meters <= best)
			{
				best = meters;
				threat = at;
				found = true;
			}
		}

		return found;
	}

	/// <summary>Whether <paramref name="candidate"/> may escort <paramref name="charge"/>: the builder stands inside its leash.</summary>
	public static bool InReach(in EscortCandidate candidate, in EscortCharge charge) =>
		Flat(candidate.Anchor, charge.Position) <= candidate.LeashMeters;

	/// <summary>
	/// Who escorts whom (docs/TACTICS.md §5.10): for each builder — the ones fired upon first,
	/// then the rest, each in the order given — up to <paramref name="perBuilder"/> of the
	/// candidates that have it in reach and escort nobody yet, nearest it first, measured flat;
	/// one that escorted it on the last pass counts <see cref="KeepMeters"/> nearer, and ties
	/// go to the lower unit id. Writes, per candidate, the index of its builder or -1 into
	/// <paramref name="chargeOf"/> and its place among that builder's escorts into
	/// <paramref name="slotOf"/>; per builder, how many escort it into <paramref name="countOf"/>.
	/// Returns how many escort anyone.
	/// </summary>
	public static int Assign(ReadOnlySpan<EscortCharge> charges, ReadOnlySpan<EscortCandidate> candidates,
		Span<int> chargeOf, Span<int> slotOf, Span<int> countOf, int perBuilder = PerBuilder)
	{
		int candidateCount = Math.Min(candidates.Length, Math.Min(chargeOf.Length, slotOf.Length));
		for (int i = 0; i < candidateCount; i++)
		{
			chargeOf[i] = -1;
			slotOf[i] = 0;
		}

		int chargeCount = Math.Min(charges.Length, countOf.Length);
		for (int b = 0; b < chargeCount; b++)
		{
			countOf[b] = 0;
		}

		int assigned = 0;
		for (int pass = 0; pass < 2; pass++)
		{
			for (int b = 0; b < chargeCount; b++)
			{
				if (charges[b].FiredUpon != (pass == 0))
				{
					continue;
				}

				for (int slot = 0; slot < perBuilder; slot++)
				{
					int best = Nearest(charges[b], candidates[..candidateCount], chargeOf);
					if (best < 0)
					{
						break;
					}

					chargeOf[best] = b;
					slotOf[best] = slot;
					countOf[b]++;
					assigned++;
				}
			}
		}

		return assigned;
	}

	/// <summary>The free candidate nearest <paramref name="charge"/> that has it in reach, by <see cref="Assign"/>'s rule; -1 for none.</summary>
	private static int Nearest(in EscortCharge charge, ReadOnlySpan<EscortCandidate> candidates, ReadOnlySpan<int> chargeOf)
	{
		int best = -1;
		float bestScore = float.MaxValue;
		for (int i = 0; i < candidates.Length; i++)
		{
			EscortCandidate candidate = candidates[i];
			if (chargeOf[i] >= 0 || !InReach(candidate, charge))
			{
				continue;
			}

			float score = Flat(candidate.Position, charge.Position)
				- (candidate.Previous != 0 && candidate.Previous == charge.UnitId ? KeepMeters : 0f);
			if (best < 0 || score < bestScore || (score == bestScore && candidate.UnitId < candidates[best].UnitId))
			{
				best = i;
				bestScore = score;
			}
		}

		return best;
	}

	/// <summary>
	/// Where an escort with no wall to cover its builder from stands (docs/TACTICS.md §5.10):
	/// between the builder and the threat, <see cref="ScreenMeters"/> out from the builder —
	/// less when the threat is so near that would put it inside <see cref="ScreenStandoffMeters"/>
	/// of it — and, with more than one escort, <see cref="ScreenSpacingMeters"/> apart across
	/// the line, in slot order. A threat on top of the builder leaves the screen beside it.
	/// </summary>
	public static Vector3 ScreenPoint(Vector3 builder, Vector3 threat, int slot, int escorts)
	{
		float dx = threat.X - builder.X;
		float dz = threat.Z - builder.Z;
		float range = MathF.Sqrt((dx * dx) + (dz * dz));
		if (range < 1e-3f)
		{
			// No way to face: the screen stands along X, beside the builder.
			dx = 1f;
			dz = 0f;
			range = 1f;
		}

		float ux = dx / range;
		float uz = dz / range;
		float out_ = Math.Clamp(range - ScreenStandoffMeters, 0f, ScreenMeters);
		float across = (slot - ((Math.Max(escorts, 1) - 1) * 0.5f)) * ScreenSpacingMeters;
		return new Vector3(builder.X + (ux * out_) - (uz * across), builder.Y, builder.Z + (uz * out_) + (ux * across));
	}

	/// <summary>
	/// <paramref name="at"/>, or the point nearest it inside <paramref name="leashMeters"/> of
	/// <paramref name="anchor"/>, measured flat: an escort never stands outside its leash.
	/// </summary>
	public static Vector3 IntoLeash(Vector3 at, Vector3 anchor, float leashMeters)
	{
		float meters = Flat(at, anchor);
		if (meters <= leashMeters || meters < 1e-6f)
		{
			return at;
		}

		float keep = MathF.Max(leashMeters, 0f) / meters;
		return new Vector3(anchor.X + ((at.X - anchor.X) * keep), at.Y, anchor.Z + ((at.Z - anchor.Z) * keep));
	}

	private static float Flat(Vector3 a, Vector3 b)
	{
		float x = a.X - b.X;
		float z = a.Z - b.Z;
		return MathF.Sqrt((x * x) + (z * z));
	}
}
