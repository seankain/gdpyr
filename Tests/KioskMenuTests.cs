using System;
using System.Collections.Generic;
using Gdpyr.Sim;
using Godot;
using Xunit;

namespace Gdpyr.Tests;

/// <summary>
/// The vehicle kiosk's panel, the HUD's kiosk line and where the kiosk puts a vehicle on
/// its pad (docs/GROUND_VEHICLES.md §6.6, §8.5).
/// </summary>
public class KioskMenuTests
{
	private static readonly KioskLine Ready = new(KioskStatus.Ready, 0);
	private static readonly KioskLine Out = new(KioskStatus.Out, 0);
	private static readonly KioskLine Off = new(KioskStatus.Disabled, 0);

	private static KioskLine Counting(int seconds) => new(KioskStatus.Counting, seconds);

	private static KioskMenu Opened()
	{
		var menu = new KioskMenu();
		menu.Open();
		return menu;
	}

	private static KioskMenuSituation AtTheKiosk(bool alive = true, bool ground = true, bool role = false,
		bool ended = false, bool seated = false, float distance = 0.4f) =>
		new(alive, ground, role, ended, seated, distance);

	// ---- the keys -------------------------------------------------------------------------

	[Fact]
	public void OneAsksForA4x4_TwoForAHelicopter_AndEOrEscapeClose()
	{
		Assert.Equal(KioskAction.Ask4x4, KioskMenu.ActionOf(Key.Key1));
		Assert.Equal(KioskAction.Ask4x4, KioskMenu.ActionOf(Key.Kp1));
		Assert.Equal(KioskAction.AskHelicopter, KioskMenu.ActionOf(Key.Key2));
		Assert.Equal(KioskAction.AskHelicopter, KioskMenu.ActionOf(Key.Kp2));
		Assert.Equal(KioskAction.Close, KioskMenu.ActionOf(Key.E));
		Assert.Equal(KioskAction.Close, KioskMenu.ActionOf(Key.Escape));
		Assert.Equal(KioskAction.None, KioskMenu.ActionOf(Key.Key3));
		Assert.Equal(KioskAction.None, KioskMenu.ActionOf(Key.Enter));
		Assert.Equal(KioskAction.None, KioskMenu.ActionOf(Key.W));

		Assert.Equal((byte)CrewVehicleKind.FourByFour, KioskMenu.KindOf(KioskAction.Ask4x4));
		Assert.Equal((byte)CrewVehicleKind.Helicopter, KioskMenu.KindOf(KioskAction.AskHelicopter));
	}

	[Fact]
	public void AClosedPanel_DoesNothing()
	{
		var menu = new KioskMenu();
		Assert.False(menu.IsOpen);
		Assert.Equal(KioskOutcome.None, menu.Apply(KioskAction.Ask4x4));
		Assert.Equal(KioskOutcome.None, menu.Apply(KioskAction.Close));
		Assert.Equal(KioskOutcome.None, menu.Answer(KioskRefusal.None));
		Assert.False(menu.StillWanted(AtTheKiosk()));
	}

	// ---- asking and the answer ------------------------------------------------------------

	[Fact]
	public void Asking_SendsTheKind_AndWaitsForTheAnswerBeforeAskingAgain()
	{
		KioskMenu menu = Opened();
		Assert.Equal(KioskOutcome.Ask, menu.Apply(KioskAction.AskHelicopter));
		Assert.Equal((byte)CrewVehicleKind.Helicopter, menu.AskedKind);
		Assert.True(menu.Awaiting);
		Assert.False(menu.IsEnabled(KioskAction.Ask4x4));
		Assert.Equal(KioskOutcome.None, menu.Apply(KioskAction.Ask4x4));
		Assert.Equal((byte)CrewVehicleKind.Helicopter, menu.AskedKind);
		Assert.True(menu.IsEnabled(KioskAction.Close));
		Assert.Equal("asking the kiosk for a helicopter…", menu.StatusLine(new[] { Ready, Ready }));
	}

	[Fact]
	public void Taken_ClosesThePanel()
	{
		KioskMenu menu = Opened();
		menu.Apply(KioskAction.Ask4x4);
		Assert.Equal(KioskOutcome.Close, menu.Answer(KioskRefusal.None));
		Assert.False(menu.IsOpen);
		Assert.False(menu.Awaiting);
	}

	[Fact]
	public void ARefusal_StaysInTheFooter_WithTheKeysLiveAgain()
	{
		KioskMenu menu = Opened();
		menu.Apply(KioskAction.Ask4x4);
		Assert.Equal(KioskOutcome.Edited, menu.Answer(KioskRefusal.Out));
		Assert.True(menu.IsOpen);
		Assert.False(menu.Awaiting);
		Assert.Equal(KioskRefusal.Out, menu.LastRefusal);
		Assert.Equal((byte)CrewVehicleKind.FourByFour, menu.RefusedKind);
		Assert.True(menu.IsEnabled(KioskAction.AskHelicopter));
		Assert.StartsWith("refused (out): ", menu.StatusLine(new[] { Out, Ready }));

		// Asking again clears it.
		menu.Apply(KioskAction.AskHelicopter);
		Assert.Equal(KioskRefusal.None, menu.LastRefusal);
	}

	[Fact]
	public void AnAnswerToNothing_IsIgnored()
	{
		KioskMenu menu = Opened();
		Assert.Equal(KioskOutcome.None, menu.Answer(KioskRefusal.NotReady));
		Assert.Equal(KioskRefusal.None, menu.LastRefusal);

		// Asked, closed and opened again: the old request's answer means nothing here.
		menu.Apply(KioskAction.Ask4x4);
		menu.Apply(KioskAction.Close);
		menu.Open();
		Assert.Equal(KioskOutcome.None, menu.Answer(KioskRefusal.None));
		Assert.True(menu.IsOpen);
	}

	[Fact]
	public void NotReady_CountsDownWithTheKindsLine_AsSection85SaysIt()
	{
		KioskMenu menu = Opened();
		menu.Apply(KioskAction.AskHelicopter);
		menu.Answer(KioskRefusal.NotReady);
		Assert.Equal("refused (not_ready): ready in 0:42", menu.StatusLine(new[] { Ready, Counting(42) }));
		Assert.Equal("refused (not_ready): ready in 0:41", menu.StatusLine(new[] { Ready, Counting(41) }));
		Assert.Equal("refused (not_ready): ready in 1:30", menu.StatusLine(new[] { Ready, Counting(90) }));
		Assert.Equal("refused (not_ready): ready now", menu.StatusLine(new[] { Ready, Ready }));

		// Explained against the kind it was for, not the other one.
		menu.Apply(KioskAction.Ask4x4);
		menu.Answer(KioskRefusal.NotReady);
		Assert.Equal("refused (not_ready): ready in 0:05", menu.StatusLine(new[] { Counting(5), Counting(80) }));
	}

	[Fact]
	public void EveryRefusal_IsExplained()
	{
		for (byte i = 1; KioskRules.TryRead(i, out KioskRefusal refusal); i++)
		{
			Assert.False(string.IsNullOrEmpty(KioskMenu.Explain(refusal, Counting(3))), $"{refusal} has no explanation");
		}

		Assert.Equal(string.Empty, KioskMenu.Explain(KioskRefusal.None, Ready));
	}

	// ---- when it closes on its own --------------------------------------------------------

	[Fact]
	public void ItIsWanted_AtTheKiosk_AliveOnFootOnTheGroundForce()
	{
		KioskMenu menu = Opened();
		Assert.True(menu.StillWanted(AtTheKiosk()));
		Assert.True(menu.StillWanted(AtTheKiosk(distance: 1f)));
	}

	[Fact]
	public void ItClosesFor_Death_ASideChange_TheRoleMenu_ASeat_LeavingTheReach_AndTheRoundsEnd()
	{
		KioskMenu menu = Opened();
		Assert.False(menu.StillWanted(AtTheKiosk(alive: false)));
		Assert.False(menu.StillWanted(AtTheKiosk(ground: false)));
		Assert.False(menu.StillWanted(AtTheKiosk(role: true)));
		Assert.False(menu.StillWanted(AtTheKiosk(seated: true)));
		Assert.False(menu.StillWanted(AtTheKiosk(distance: 1.01f)));
		Assert.False(menu.StillWanted(AtTheKiosk(distance: float.PositiveInfinity)));
		Assert.False(menu.StillWanted(AtTheKiosk(ended: true)));
	}

	// ---- the rows -------------------------------------------------------------------------

	[Fact]
	public void EachRow_SaysItsKindsKey_Name_AndStatus()
	{
		Assert.Equal("[1] 4x4 — ready", KioskMenu.RowLabel(0, Ready));
		Assert.Equal("[1] 4x4 — out", KioskMenu.RowLabel(0, Out));
		Assert.Equal("[2] helicopter — ready in 1:30", KioskMenu.RowLabel(1, Counting(90)));
		Assert.Equal("[2] helicopter — not offered here", KioskMenu.RowLabel(1, Off));
	}

	[Fact]
	public void TheClock_IsMinutesAndTwoDigitSeconds()
	{
		Assert.Equal("0:00", KioskMenu.Clock(0));
		Assert.Equal("0:00", KioskMenu.Clock(-3));
		Assert.Equal("0:09", KioskMenu.Clock(9));
		Assert.Equal("0:45", KioskMenu.Clock(45));
		Assert.Equal("1:30", KioskMenu.Clock(90));
		Assert.Equal("4:15", KioskMenu.Clock(255));
	}

	// ---- the HUD's line -------------------------------------------------------------------

	[Fact]
	public void TheHudsLine_IsEveryKindOffered_AsSection85SaysIt()
	{
		Assert.Equal("4x4 ready · helicopter 0:42", KioskHud.Line(new[] { Ready, Counting(42) }));
		Assert.Equal("4x4 out · helicopter ready", KioskHud.Line(new[] { Out, Ready }));
		Assert.Equal("helicopter 1:30", KioskHud.Line(new[] { Off, Counting(90) }));
		Assert.Equal(string.Empty, KioskHud.Line(new[] { Off, Off }));
		Assert.Equal(string.Empty, KioskHud.Line(ReadOnlySpan<KioskLine>.Empty));
	}

	[Fact]
	public void TheDebugLine_NamesAKindTheKioskDoesNotOffer()
	{
		Assert.Equal("4x4 out · helicopter 0:42", KioskHud.DebugLine(new[] { Out, Counting(42) }));
		Assert.Equal("4x4 off · helicopter ready", KioskHud.DebugLine(new[] { Off, Ready }));
		Assert.Equal("4x4 off · helicopter off", KioskHud.DebugLine(new[] { KioskLine.None, KioskLine.None }));
	}

	[Fact]
	public void TheKindsNames_AreTheDefinitionsNames()
	{
		Dictionary<string, string> fourByFour = CrewVehicleFiles.Resource(CrewVehicleFiles.FourByFourPath);
		Dictionary<string, string> helicopter = CrewVehicleFiles.Resource(CrewVehicleFiles.HelicopterPath);
		Assert.Equal($"&\"{KioskMenu.NameOf(0)}\"", fourByFour["Name"]);
		Assert.Equal($"&\"{KioskMenu.NameOf(1)}\"", helicopter["Name"]);
		Assert.Equal(CrewVehicleCodec.Kinds, KioskMenu.Kinds);
	}

	// ---- the pad --------------------------------------------------------------------------

	[Fact]
	public void AVehicleOnItsPad_FacesTheMarkersMinusZ()
	{
		for (int i = 0; i < 16; i++)
		{
			float markerYaw = (i * 0.41f) - 3f;
			var marker = new Basis(Vector3.Up, markerYaw);
			float yaw = KioskRules.PadYaw(marker.Z);
			Vector3 forward = new CrewPose(Vector3.Zero, yaw).Forward;
			Assert.True(forward.IsEqualApprox(-marker.Z), $"marker at {markerYaw}: facing {forward}, not {-marker.Z}");
		}
	}

	[Fact]
	public void The4x4StandsOnItsSprings_AndTheHelicopterOnItsSkids()
	{
		var ground = new Vector3(-92.4f, 0.5f, 103.4f);
		Assert.Equal(ground + new Vector3(0f, 0.703f, 0f), KioskRules.PadOrigin(CrewVehicleKind.FourByFour, ground, 0.703f));
		Assert.Equal(ground, KioskRules.PadOrigin(CrewVehicleKind.Helicopter, ground, 0.703f));
	}
}
