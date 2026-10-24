using System;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// A unit's posture (docs/TACTICS.md §5.8): a crouched body is lower, sees from lower,
/// walks at half speed, and behind a sandbag wall is out of every line a standing
/// player can draw at it — and can draw none back.
/// </summary>
public class UnitBodyTests
{
	/// <summary>infantry.tres: 1.8 m, 0.4 m across, its eye at 1.5; 1.0 m crouched, its eye at 0.85.</summary>
	private static readonly UnitBody Rifleman = new(1.8f, 0.4f, 1.5f, 1.0f, 0.85f);

	/// <summary>A tank: no crouch in its file.</summary>
	private static readonly UnitBody Tank = new(3f, 1.3f, 1.9f);

	/// <summary>sandbag_wall.tres's body: 4 m across, 1.1 m high, 0.8 m deep.</summary>
	private static readonly Vector3 SandbagWall = new(4f, 1.1f, 0.8f);

	/// <summary>A ground-force player's standing eye above the ground: the camera's 1.5 m.</summary>
	private const float PlayerEyeMeters = 1.5f;

	[Fact]
	public void Crouched_ItIsLower_SeesFromLower_AndWalksAtHalfSpeed()
	{
		Assert.True(Rifleman.CanCrouch);
		Assert.Equal(UnitPosture.Crouched, Rifleman.Take(UnitPosture.Crouched));
		Assert.Equal(1.0f, Rifleman.Height(UnitPosture.Crouched));
		Assert.Equal(0.85f, Rifleman.EyeHeight(UnitPosture.Crouched));
		Assert.Equal(0.5f, Rifleman.SpeedFactor(UnitPosture.Crouched));
		Assert.Equal(0.5f, Rifleman.ProtectMeters(UnitPosture.Crouched));

		Assert.Equal(1.8f, Rifleman.Height(UnitPosture.Standing));
		Assert.Equal(1.5f, Rifleman.EyeHeight(UnitPosture.Standing));
		Assert.Equal(1f, Rifleman.SpeedFactor(UnitPosture.Standing));
		Assert.Equal(0.9f, Rifleman.ProtectMeters(UnitPosture.Standing));
	}

	[Fact]
	public void TheCrouchedHitbox_IsTheLowerCapsule_FromTheSameFeet()
	{
		var feet = new Vector3(3f, 0.5f, -2f);
		HitCapsule crouched = Rifleman.Hitbox(feet, UnitPosture.Crouched);
		HitCapsule standing = Rifleman.Hitbox(feet, UnitPosture.Standing);

		Assert.Equal(0.4f, crouched.Radius);
		Assert.Equal(feet.Y + 1.0f, crouched.Top.Y + crouched.Radius, 4);
		Assert.Equal(feet.Y, crouched.Bottom.Y - crouched.Radius, 4);
		Assert.Equal(feet.Y + 1.8f, standing.Top.Y + standing.Radius, 4);
	}

	[Fact]
	public void ABodyThatCannotCrouch_Stands_AndSoDoesOneWhoseNumbersAreNoCrouch()
	{
		Assert.False(Tank.CanCrouch);
		Assert.Equal(UnitPosture.Standing, Tank.Take(UnitPosture.Crouched));
		Assert.Equal(3f, Tank.Height(UnitPosture.Crouched));
		Assert.Equal(1f, Tank.SpeedFactor(UnitPosture.Crouched));

		Assert.False(new UnitBody(1.8f, 0.4f, 1.5f, 1.8f, 0.85f).CanCrouch, "no lower than standing");
		Assert.False(new UnitBody(1.8f, 0.4f, 1.5f, 1.0f, 1.2f).CanCrouch, "its eye above its top");
		Assert.False(new UnitBody(1.8f, 0.4f, 1.5f, 1.0f, 0f).CanCrouch, "no eye");
	}

	[Theory]
	[InlineData(5f)]
	[InlineData(10f)]
	[InlineData(20f)]
	[InlineData(40f)]
	[InlineData(60f)]
	public void BehindASandbagWall_Crouched_NoLineFromAStandingPlayerReachesIt_AndStandingItsHeadShows(float range)
	{
		// The wall's middle on the origin, its broad side to +Z; the unit in a cover spot
		// behind it, its body clear of the wall by the board's gap.
		var board = new CoverBoard();
		board.SetStructure(0, Vector3.Zero, 0f, SandbagWall);
		var feet = new Vector3(0f, 0f, -(SandbagWall.Z * 0.5f) - CoverBoard.GapMeters - 0.4f);

		foreach (float across in new[] { -6f, -2f, 0f, 1.5f, 4f })
		{
			var eye = new Vector3(across, PlayerEyeMeters, range);
			Assert.Equal(0, Exposed(board, eye, Rifleman.Hitbox(feet, UnitPosture.Crouched)));
			Assert.True(Exposed(board, eye, Rifleman.Hitbox(feet, UnitPosture.Standing)) > 0,
				$"standing, nothing of it shows from {range} m, {across} m across");
		}
	}

	[Fact]
	public void BehindASandbagWall_Crouched_ItCannotSeeOverIt()
	{
		var board = new CoverBoard();
		board.SetStructure(0, Vector3.Zero, 0f, SandbagWall);
		var feet = new Vector3(0f, 0f, -1f);

		// A standing player's chest, 20 m off: hidden from the crouched eye, seen from the standing one.
		var chest = new Vector3(0f, 1f, 20f);
		Assert.True(board.FirstHit(feet + (Vector3.Up * Rifleman.EyeHeight(UnitPosture.Crouched)), chest, out _) >= 0);
		Assert.Equal(-1, board.FirstHit(feet + (Vector3.Up * Rifleman.EyeHeight(UnitPosture.Standing)), chest, out _));
	}

	[Fact]
	public void ABoxThatLeavesItStandingInTheOpen_HidesItCrouched()
	{
		// docs/TACTICS.md §5.8: the protect height is the crouched chest. A box of the
		// board's lowest height: over a standing chest's line, under a crouched one's.
		var board = new CoverBoard();
		var box = new Vector3(4f, CoverBoard.MinHeightMeters, 0.8f);
		Assert.Equal(0, board.AddLevel(new Vector3(0f, box.Y * 0.5f, 0f), 0f, box));
		var at = new Vector3(0f, 0f, -0.8f);

		CoverQuery Query(UnitPosture posture) => new()
		{
			From = at,
			Threat = new Vector3(0f, 0f, 20f),
			ThreatEyeMeters = 1.5f,
			BodyRadiusMeters = Rifleman.RadiusMeters,
			ProtectMeters = Rifleman.ProtectMeters(posture),
		};

		Assert.False(board.Protects(at, Query(UnitPosture.Standing)));
		Assert.True(board.Protects(at, Query(UnitPosture.Crouched)));
	}

	/// <summary>
	/// How many lines from <paramref name="eye"/> strike the capsule before any box: aimed
	/// through a centimetre grid on a plane across the capsule's axis, its width and from
	/// its feet to above its top, and tested as a round is (<see cref="Hitbox.SegmentIntersects"/>).
	/// </summary>
	private static int Exposed(CoverBoard board, Vector3 eye, in HitCapsule body)
	{
		int exposed = 0;
		Vector3 axis = body.Bottom;
		Vector3 toward = new Vector3(eye.X - axis.X, 0f, eye.Z - axis.Z).Normalized();
		Vector3 across = toward.Cross(Vector3.Up);
		float top = body.Top.Y + body.Radius;
		float feet = body.Bottom.Y - body.Radius;
		for (float y = feet; y <= top + 0.1f; y += 0.01f)
		{
			for (float x = -body.Radius; x <= body.Radius; x += 0.01f)
			{
				Vector3 aim = new Vector3(axis.X, y, axis.Z) + (across * x);
				Vector3 far = eye + ((aim - eye) * 2f);
				if (Hitbox.SegmentIntersects(body, eye, far, 0f, out float t)
					&& board.FirstHit(eye, eye + ((far - eye) * t), out _) < 0)
				{
					exposed++;
				}
			}
		}

		return exposed;
	}
}
