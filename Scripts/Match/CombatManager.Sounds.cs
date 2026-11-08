using System;
using Gdpyr.Core;
using Gdpyr.Effects;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Match;

/// <summary>
/// Weapon sounds (docs/AUDIO.md): when a shot is heard, and when a round is heard going
/// past. What they sound like and how they are played is <see cref="WeaponAudio"/>'s;
/// this half only decides when, from the events the simulation already has — a spawn, a
/// prediction, a chord — and adds nothing to the wire.
/// </summary>
public partial class CombatManager
{
	/// <summary>Null on a process that plays nothing, which every method here begins by asking.</summary>
	private WeaponAudio _sounds;

	/// <summary>The rounds already heard going past, so a slow one is heard once however many chords it takes.</summary>
	private readonly RecentRounds _heardGoingBy = new();

	/// <summary>
	/// A shot by anyone, from where it left the muzzle (§2.1): heard at the ear when the
	/// local player fired it, in 3D at <paramref name="origin"/> otherwise, and its muzzle
	/// effects drawn (docs/MUZZLE_EFFECTS.md §2). On the authority — a listen host, an
	/// offline round — a strategist here neither hears nor sees a shot its side's sensors do
	/// not cover, as it is not sent one on a client (<see cref="Broadcast"/>).
	/// </summary>
	private void PresentShot(int ownerId, byte definitionId, Vector3 origin, Vector3 direction, bool authoritative)
	{
		if (_sounds == null && _muzzles == null)
		{
			return;
		}

		if (authoritative && Local is { Team: Team.Strategist } && !Visibility.Covers(origin))
		{
			return;
		}

		bool own = IsLocalOwner(ownerId);
		_sounds?.PlayShot(definitionId, origin, own);
		if (own)
		{
			PresentOwnMuzzle(definitionId, origin, direction);
		}
		else
		{
			PresentMuzzle(ownerId, definitionId, origin, direction);
		}
	}

	/// <summary>
	/// The local player's own shot, on the tick their client predicted it — with the tracer,
	/// a round trip before the server's spawn, which is then neither heard nor drawn again
	/// (§2.1).
	/// </summary>
	private void PresentOwnShot(byte definitionId, Vector3 origin, Vector3 direction)
	{
		_sounds?.PlayShot(definitionId, origin, own: true);
		PresentOwnMuzzle(definitionId, origin, direction);
	}

	/// <summary>Whether <paramref name="ownerId"/> is the local player.</summary>
	private bool IsLocalOwner(int ownerId) => Local != null && ownerId == OwnerId.ForPeer(Local.PeerId);

	/// <summary>
	/// Every round that went past the listener on the tick just flown (§2.2): one chord at a
	/// time against the camera, after the authority has stopped what hit something, so a
	/// round that struck a wall on its way is not heard on the far side of it. Not the local
	/// player's own rounds, and nothing for a strategist, whose camera is above the field
	/// rather than in it.
	/// </summary>
	private void ListenForFlybys()
	{
		if (_sounds == null || _projectiles == null || Local is { Team: Team.Strategist })
		{
			return;
		}

		if (GetViewport()?.GetCamera3D() is not { } camera)
		{
			return;
		}

		Vector3 ear = camera.GlobalPosition;
		int own = Local != null ? OwnerId.ForPeer(Local.PeerId) : 0;
		ReadOnlySpan<ProjectileSegment> segments = _projectiles.Segments;

		for (int i = 0; i < segments.Length; i++)
		{
			ProjectileSegment segment = segments[i];
			if (segment.Resting || (own != 0 && segment.OwnerPeerId == own))
			{
				continue;
			}

			FlybyKind kind = WeaponCatalog.FlybyOf(segment.DefinitionId);
			if (kind == FlybyKind.None
				|| !Flyby.Passes(kind, segment.From, segment.To, segment.Origin, ear, out Vector3 point))
			{
				continue;
			}

			// Geometry first: almost every chord is turned away above, and these two are a
			// search each.
			if (_heardGoingBy.TryGet(segment.Id, out _) || !_projectiles.TryGet(segment.Id, out _))
			{
				continue;
			}

			_heardGoingBy.Note(segment.Id, segment.DefinitionId);
			_sounds.PlayFlyby(segment.DefinitionId, point);
		}
	}
}
