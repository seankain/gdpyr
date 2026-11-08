using Gdpyr.Core;
using Gdpyr.Effects;
using Gdpyr.Fps;
using Gdpyr.Rts;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Match;

/// <summary>
/// Muzzle effects (docs/MUZZLE_EFFECTS.md): where a shot's flash, smoke and spent case
/// are drawn from, and what they follow. How they look and which of them are drawn is
/// <see cref="MuzzleEffects"/>'s; this half only finds the muzzle, from the events the
/// simulation already has — the same spawn and prediction a shot is heard from
/// (<see cref="PresentShot"/>, <see cref="PresentOwnShot"/>) — and adds nothing to the wire.
/// </summary>
public partial class CombatManager
{
	/// <summary>Null on a process that draws nothing, which every method here begins by asking.</summary>
	private MuzzleEffects _muzzles;

	/// <summary>
	/// Anyone else's shot (§2.1), drawn from where its shooter is drawn now rather than
	/// from where the server fired it: a body's muzzle held out ahead of its eye, a vehicle
	/// unit's at the edge of its hull, a mounted gun's at its own muzzle. Whether any of
	/// it is drawn — how far from the camera it is — is the effects' to decide.
	/// </summary>
	private void PresentMuzzle(int ownerId, byte definitionId, Vector3 origin, Vector3 direction)
	{
		if (_muzzles == null)
		{
			return;
		}

		ShotKind kind = WeaponCatalog.ShotOf(definitionId);
		MuzzleProfile profile = Muzzles.Profile(kind);
		if (profile.IsNone)
		{
			return;
		}

		Node3D anchor = null;
		Vector3 muzzle = origin;
		Vector3 velocity = Vector3.Zero;

		if (OwnerId.IsPeer(ownerId) && Find(ownerId)?.Character is { } character)
		{
			// A player's round leaves from their eye, a mounted gun's from its muzzle.
			anchor = character;
			velocity = character.Velocity;
			if (profile.HandHeld)
			{
				muzzle = Muzzles.HeldMuzzle(character.EyePosition, direction, profile);
			}
		}
		else if (OwnerId.IsUnit(ownerId) && UnitManager.Instance?.Find(OwnerId.UnitOf(ownerId)) is { } unit)
		{
			// Every unit's round leaves from its eye: a rifleman's from his head, a tank's
			// from the middle of its hull.
			anchor = unit;
			velocity = unit.Velocity;
			muzzle = unit.Definition is { IsVehicle: true } vehicle
				? Muzzles.HullMuzzle(unit.EyePosition, direction, vehicle.RadiusMeters)
				: Muzzles.HeldMuzzle(unit.EyePosition, direction, profile);
		}

		// A barracks defence's or a structure's round already leaves from its muzzle.
		_muzzles.Emit(kind, muzzle, direction, anchor, velocity, own: false);
	}

	/// <summary>
	/// The local player's own shot (§2.2), drawn whole wherever the camera is: from the end
	/// of the viewmodel's barrel and following it, for a weapon in their hands while it is
	/// drawn; from a mounted or seated gun's muzzle for one of those; and, with a scope's
	/// eyepiece up, from where a body would hold the muzzle, following the camera.
	/// </summary>
	private void PresentOwnMuzzle(byte definitionId, Vector3 origin, Vector3 direction)
	{
		if (_muzzles == null)
		{
			return;
		}

		ShotKind kind = WeaponCatalog.ShotOf(definitionId);
		MuzzleProfile profile = Muzzles.Profile(kind);
		if (profile.IsNone)
		{
			return;
		}

		fps_controller character = Local?.Character;
		Vector3 velocity = character?.Velocity ?? Vector3.Zero;
		if (!profile.HandHeld)
		{
			_muzzles.Emit(kind, origin, direction, character, velocity, own: true);
			return;
		}

		if (character?.Viewmodel is { } viewmodel && viewmodel.TryGetMuzzle(out Vector3 barrel, out Vector3 forward))
		{
			_muzzles.Emit(kind, barrel, forward, viewmodel, velocity, own: true);
			return;
		}

		Node3D camera = GetViewport()?.GetCamera3D();
		_muzzles.Emit(kind, Muzzles.HeldMuzzle(origin, direction, profile), direction, camera ?? character, velocity,
			own: true);
	}
}
