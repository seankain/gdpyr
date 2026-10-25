using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// Being fired upon (docs/TACTICS.md §4.1): every round the other side sends at or
/// past a unit, every swing and every blast that reaches one, written into the
/// unit's <see cref="Unit.Fire"/> for its planner to read. Called by
/// <c>CombatManager</c> as it resolves each chord, with no ray of its own: a near
/// miss is geometry against the capsules the units already have.
///
/// Under <c>--bot-ai htn</c> only. Legacy units plan nothing, and the pass would be
/// cost with no reader (docs/HTN_BOTS.md D5).
/// </summary>
public partial class UnitManager
{
	/// <summary>
	/// The broad phase's allowance for a blast, which reaches a body's surface: the
	/// widest unit's radius, the tank's 2 m. A unit wider than this would have a blast's
	/// reach on it measured short by the difference.
	/// </summary>
	private const float MaxUnitRadiusMeters = 2f;

	/// <summary>Rounds that went past a unit this round without hitting it, counted per unit. Server-side, for the debug HUD.</summary>
	public int NearMissesNoted { get; private set; }

	/// <summary>Rounds, swings and blasts that reached a unit this round, counted per unit. Server-side, for the debug HUD.</summary>
	public int HitsNoted { get; private set; }

	/// <summary>
	/// One chord of a round, or a swing, from <paramref name="from"/> to where it
	/// stopped (<paramref name="to"/>): a hit on <paramref name="victim"/>, a near miss
	/// on every other unit of the other side it passed within <see cref="NearMiss.Meters"/>
	/// of, and — for a round that went off where it stopped — a hit on every one its
	/// blast reached.
	/// </summary>
	public void NoteRound(in FireSource source, Team shooterTeam, Vector3 from, Vector3 to, Unit victim,
		float blastRadius, uint tick)
	{
		if (_planning == null)
		{
			return;
		}

		// One box for the chord, grown by a blast's reach when it has one, and a capsule
		// built only for a unit inside it.
		var chord = new NearMiss.Chord(from, to,
			blastRadius > 0f ? Mathf.Max(NearMiss.Meters, blastRadius + MaxUnitRadiusMeters) : NearMiss.Meters);
		for (int i = 0; i < _ordered.Count; i++)
		{
			Unit unit = _ordered[i];
			if (!unit.IsOnField || unit.Team == shooterTeam)
			{
				continue;
			}

			if (unit != victim && !chord.MayPass(unit.GlobalPosition, unit.Definition?.HeightMeters ?? 1.8f))
			{
				continue;
			}

			HitCapsule body = unit.Hitbox;
			FireKind kind = unit == victim || NearMiss.InReach(body, to, blastRadius) ? FireKind.Hit
				: NearMiss.Passes(body, from, to) ? FireKind.NearMiss
				: FireKind.None;
			Note(unit, source, kind, tick);
		}
	}

	/// <summary>
	/// A fused round at <paramref name="point"/>: <see cref="FireKind.NearMiss"/> for
	/// one that has come to rest — a live grenade is a reason to move — and
	/// <see cref="FireKind.Hit"/> for its blast, on every unit of the other side within
	/// its reach.
	/// </summary>
	public void NoteBlast(in FireSource source, Team shooterTeam, Vector3 point, float radius, FireKind kind,
		uint tick)
	{
		if (_planning == null || kind == FireKind.None)
		{
			return;
		}

		for (int i = 0; i < _ordered.Count; i++)
		{
			Unit unit = _ordered[i];
			if (unit.IsOnField && unit.Team != shooterTeam && NearMiss.InReach(unit.Hitbox, point, radius))
			{
				Note(unit, source, kind, tick);
			}
		}
	}

	/// <summary>Units fired upon within the last <see cref="FiredUpon.WindowTicks"/>. Server-side, for the debug HUD.</summary>
	public int UnitsFiredUpon(uint tick)
	{
		int count = 0;
		for (int i = 0; i < _ordered.Count; i++)
		{
			Unit unit = _ordered[i];
			count += unit.IsOnField && FiredUpon.IsRecent(unit.Fire, tick) ? 1 : 0;
		}

		return count;
	}

	/// <summary>
	/// The debug HUD's <c>fire</c> row: who is under fire now, what this round has sent at
	/// the units, the grenades they have thrown back at enemies hidden behind cover
	/// (docs/TACTICS.md §5.4), and the gunners firing at places they cannot see (§5.2).
	/// </summary>
	public string DescribeFire(uint tick) =>
		$"{UnitsFiredUpon(tick)} under fire  {NearMissesNoted} near misses  {HitsNoted} hits  {HiddenThrows} grenades over cover this round  {GunnersSuppressing()} gunners suppressing";

	/// <summary>What one unit makes of one round: where it reckons it came from, and how close it was.</summary>
	private void Note(Unit unit, in FireSource source, FireKind kind, uint tick)
	{
		if (kind == FireKind.None)
		{
			return;
		}

		Vector3 perceived = FiredUpon.Perceive(unit.EyePosition, source.Origin, FiredUpon.ErrorFraction,
			FiredUpon.Seed(OwnerId.ForUnit(unit.UnitId), source));
		FiredUpon.Record(ref unit.Fire, kind, perceived, source.OwnerId, tick);

		if (kind == FireKind.Hit)
		{
			HitsNoted++;
		}
		else
		{
			NearMissesNoted++;
		}
	}

	/// <summary>A new round: nothing has been fired at anybody yet.</summary>
	private void ResetFire()
	{
		NearMissesNoted = 0;
		HitsNoted = 0;
	}
}
