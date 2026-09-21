using System;
using System.Collections.Generic;
using Gdpyr.Fps;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Match;

/// <summary>
/// What the strategist is allowed to know (docs/IMPLEMENTATION_PLAN.md §M4,
/// docs/NETCODE.md §6.2).
///
/// Fog of war is an anti-cheat property, not a shader: if the packet carries the
/// position, a modified client can draw it. So this runs on the server and the
/// answer it produces is used to leave records *out* of a peer's snapshot — the
/// filter §6.1's packed replicator was chosen to make possible.
///
/// The rule is the one the plan states: a ground-force player is visible to the
/// strategist iff one of the strategist's units has it inside its
/// <c>SensorRadius</c> and can see it. Visibility is therefore a property of the
/// *side*, not of a peer — two strategists share an army and so share its eyes —
/// which is what keeps this to one recomputation rather than one per peer.
///
/// Recomputed every <see cref="SimConfig.FogRefreshIntervalTicks"/> rather than
/// every tick: fifty sensors against six players is cheap, but the line-of-sight
/// rays are not, and an eighth of a second of staleness is worth 0.6 m of a
/// walking unit.
///
/// It is not engine-free, because a wall is the engine's business
/// (<see cref="VisionField"/> is the half of it that is, and is tested). The one
/// engine dependency is injected as a ray predicate, so which physics world it
/// asks stays the caller's problem.
/// </summary>
public sealed class VisibilityService
{
	/// <summary>Where a contact was last seen, and when. Server-side; a client infers its own (docs/NETCODE.md §6.2).</summary>
	public struct Contact
	{
		/// <summary>True while a sensor has it right now.</summary>
		public bool Visible;

		/// <summary>The last tick it was visible on. Meaningless until it has been seen once.</summary>
		public uint LastSeenTick;

		/// <summary>Where it was on that tick.</summary>
		public Vector3 LastKnown;

		/// <summary>False for a player no unit has ever laid eyes on.</summary>
		public bool Known;
	}

	private readonly VisionField _sensors = new(SimConfig.MaxUnits);
	private readonly Dictionary<int, Contact> _contacts = new();
	private readonly int[] _candidates = new int[SimConfig.FogLineOfSightCandidates];
	private readonly Func<Vector3, Vector3, bool> _lineOfSight;

	/// <summary>
	/// <paramref name="lineOfSight"/> answers "is the line from here to there clear
	/// of world geometry". Null disables the ray and leaves the fog a pure range
	/// test, which is what a headless process with no physics world gets.
	/// </summary>
	public VisibilityService(Func<Vector3, Vector3, bool> lineOfSight) => _lineOfSight = lineOfSight;

	/// <summary>Sensors in the strategist's field at the last refresh. For the debug HUD.</summary>
	public int SensorCount => _sensors.Count;

	/// <summary>Ground-force players the strategist can see right now.</summary>
	public int VisibleContacts { get; private set; }

	/// <summary>Ground-force players on the field, seen or not.</summary>
	public int TrackedContacts { get; private set; }

	/// <summary>Line-of-sight rays spent since the round started. The cost of the fog, in one number.</summary>
	public int LineOfSightRays { get; private set; }

	/// <summary>Snapshot records and projectile messages the fog has kept off the wire.</summary>
	public int WithheldRecords { get; private set; }

	/// <summary>The tick the field was last rebuilt on.</summary>
	public uint LastRefreshTick { get; private set; }

	/// <summary>
	/// Recomputes the field when one is due. Called every server tick by
	/// <see cref="CombatManager.ServerPostTick"/>, after the units have moved and
	/// before the snapshot that carries the result goes out.
	/// </summary>
	public void ServerTick(uint tick, CombatManager combat, UnitManager units)
	{
		if (tick % SimConfig.FogRefreshIntervalTicks != 0)
		{
			return;
		}

		Recompute(tick, combat, units);
	}

	/// <summary>Whether a strategist may be told where this peer is right now.</summary>
	public bool IsVisible(int peerId) => _contacts.TryGetValue(peerId, out Contact contact) && contact.Visible;

	/// <summary>
	/// Whether any strategist sensor has this point in range, ignoring walls.
	///
	/// This is the test for a *message about a place* rather than about an entity —
	/// a tracer leaving a muzzle, a round striking a wall — and it deliberately
	/// spends no ray: gunfire thirty metres from a rifleman is something the
	/// strategist's man on the ground notices whether or not there is a wall between
	/// them, and a ray per shot would be a ray per shot.
	/// </summary>
	public bool Covers(Vector3 point) => _sensors.Sees(point);

	/// <summary>
	/// Where a peer was last seen and how many ticks ago, for the strategist's
	/// ghost markers on a listen host. A client derives the same two numbers from
	/// the records it has stopped receiving (docs/NETCODE.md §6.2).
	/// </summary>
	public bool TryContact(int peerId, uint tick, out Vector3 position, out float ageTicks)
	{
		position = Vector3.Zero;
		ageTicks = float.MaxValue;

		if (!_contacts.TryGetValue(peerId, out Contact contact) || !contact.Known)
		{
			return false;
		}

		position = contact.LastKnown;
		ageTicks = contact.Visible ? 0f : (float)(tick - contact.LastSeenTick);
		return true;
	}

	/// <summary>
	/// Whether <paramref name="record"/> belongs in the packet built for
	/// <paramref name="viewerPeerId"/>, a strategist.
	///
	/// Three records always go in: the viewer's own, because its prediction is
	/// reconciled against it and its health comes from it; every strategist's,
	/// because they are on the viewer's side; and a visible ground-force player's.
	/// Everything else is a record that is not written, and the caller reports how
	/// many through <see cref="CountWithheld"/>.
	/// </summary>
	public bool IsVisibleTo(int viewerPeerId, in PlayerSnapshot record)
	{
		if (record.PeerId == viewerPeerId || WeaponFlags.TeamOf(record.WeaponFlags) == Team.Strategist)
		{
			return true;
		}

		return IsVisible(record.PeerId);
	}

	/// <summary>Counts a message the fog kept off the wire, for the debug HUD.</summary>
	public void CountWithheld(int messages) => WithheldRecords += messages;

	/// <summary>Drops a peer's contact. Called when they leave, so a rejoin is not born visible.</summary>
	public void Forget(int peerId) => _contacts.Remove(peerId);

	/// <summary>Clears the field between rounds.</summary>
	public void Clear()
	{
		_contacts.Clear();
		_sensors.Clear();
		VisibleContacts = 0;
		TrackedContacts = 0;
		WithheldRecords = 0;
		LineOfSightRays = 0;
	}

	private void Recompute(uint tick, CombatManager combat, UnitManager units)
	{
		LastRefreshTick = tick;
		BuildSensors(units);

		int visible = 0;
		int tracked = 0;

		for (int i = 0; combat != null && i < combat.PlayerCount; i++)
		{
			PlayerCombat player = combat.PlayerAt(i);
			if (player?.Character == null || player.Team != Team.GroundForce)
			{
				continue;
			}

			tracked++;

			// The chest, not the eyes: it is the middle of the capsule a unit would be
			// shooting at, and a player crouching behind a crate should not be visible
			// because the top of their head clears it.
			bool seen = Sees(player.Character.Hitbox.Center);
			if (seen)
			{
				visible++;
			}

			_contacts.TryGetValue(player.PeerId, out Contact contact);
			contact.Visible = seen;
			if (seen)
			{
				contact.Known = true;
				contact.LastSeenTick = tick;

				// Recorded at the feet, not at the chest the test used: this is what the
				// strategist's marker is drawn from, and a client — which has only the
				// position the snapshot carried — has the feet too.
				contact.LastKnown = player.Character.SimPosition;
			}
			_contacts[player.PeerId] = contact;
		}

		VisibleContacts = visible;
		TrackedContacts = tracked;
	}

	/// <summary>
	/// The strategist's eyes: every unit it has alive on the field. Barracks do not
	/// see — a side with no units is blind on purpose, because that is what makes
	/// building one a decision (docs/IMPLEMENTATION_PLAN.md §M4).
	/// </summary>
	private void BuildSensors(UnitManager units)
	{
		_sensors.Clear();

		for (int i = 0; units != null && i < units.SlotCount; i++)
		{
			Unit unit = units.UnitAt(i);
			if (unit == null || !unit.IsAlive || unit.Team != Team.Strategist)
			{
				continue;
			}

			// From the eye rather than the feet, so the ray below leaves from the same
			// place the unit's own target acquisition does.
			_sensors.Add(unit.EyePosition, unit.Traits.SensorRadiusMeters);
		}
	}

	private bool Sees(Vector3 point)
	{
		int candidates = _sensors.Gather(point, _candidates);
		if (candidates == 0)
		{
			return false;
		}

		if (_lineOfSight == null)
		{
			return true;
		}

		for (int i = 0; i < candidates; i++)
		{
			LineOfSightRays++;
			if (_lineOfSight(_sensors.OriginAt(_candidates[i]), point))
			{
				return true;
			}
		}

		return false;
	}
}
