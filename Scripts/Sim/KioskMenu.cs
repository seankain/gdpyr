using System;
using Godot;

namespace Gdpyr.Sim;

/// <summary>What a key or a click on the vehicle kiosk's panel asks for (docs/GROUND_VEHICLES.md §8.5).</summary>
public enum KioskAction : byte
{
	None = 0,

	/// <summary><c>1</c>: ask for a 4x4.</summary>
	Ask4x4,

	/// <summary><c>2</c>: ask for a helicopter.</summary>
	AskHelicopter,

	/// <summary><c>E</c> or Escape.</summary>
	Close,
}

/// <summary>What the panel has to do after an action or an answer.</summary>
public enum KioskOutcome : byte
{
	/// <summary>Nothing changed.</summary>
	None = 0,

	/// <summary>Send the request for <see cref="KioskMenu.AskedKind"/> (<c>ClientRequestVehicle</c>).</summary>
	Ask,

	/// <summary>Something to draw again: a refusal in the footer.</summary>
	Edited,

	/// <summary>Take the panel down.</summary>
	Close,
}

/// <summary>What the panel's owner knows about the player at the kiosk, each tick.</summary>
public readonly struct KioskMenuSituation
{
	public readonly bool Alive;

	public readonly bool GroundForce;

	/// <summary>The role menu is up, which takes the middle of the screen and the mouse.</summary>
	public readonly bool AwaitingRole;

	public readonly bool RoundEnded;

	/// <summary>In a crew vehicle's seat.</summary>
	public readonly bool Seated;

	/// <summary>Flat distance from the player's feet to the kiosk's footprint; infinite on a map without one.</summary>
	public readonly float KioskDistance;

	public KioskMenuSituation(bool alive, bool groundForce, bool awaitingRole, bool roundEnded, bool seated,
		float kioskDistance)
	{
		Alive = alive;
		GroundForce = groundForce;
		AwaitingRole = awaitingRole;
		RoundEnded = roundEnded;
		Seated = seated;
		KioskDistance = kioskDistance;
	}
}

/// <summary>
/// The vehicle kiosk's panel, as a model (docs/GROUND_VEHICLES.md §8.5): a row per kind
/// with its status, <c>1</c> and <c>2</c> to ask, the refusal at its foot, and when the
/// panel is no longer wanted. The widget, <c>Scripts/Ui/KioskPanel.cs</c>, draws this and
/// nothing else — <see cref="LoadoutDraft"/>'s pattern, so every key, line and closing
/// rule is a question <c>dotnet test</c> answers without a window.
///
/// The panel asks whatever the status says: the server's one validator
/// (<see cref="KioskRules.Check"/>) answers, and its refusal is what the footer shows —
/// <c>refused (not_ready): ready in 0:42</c> — rather than a greyed-out row that says less.
/// </summary>
public sealed class KioskMenu
{
	/// <summary>The kinds the kiosk offers, the header's (§7.1).</summary>
	public const int Kinds = CrewVehicleCodec.Kinds;

	/// <summary>
	/// Each kind as players say it, by <see cref="CrewVehicleKind"/>: the definitions'
	/// <c>Name</c>, which <c>CrewVehicleResourceTests</c> holds these to.
	/// </summary>
	private static readonly string[] Names = { "4x4", "helicopter" };

	public bool IsOpen { get; private set; }

	/// <summary>A request has been sent and its answer has not come back; another waits for it.</summary>
	public bool Awaiting { get; private set; }

	/// <summary>The kind the last request asked for.</summary>
	public byte AskedKind { get; private set; }

	/// <summary>The refusal the last request got; <see cref="KioskRefusal.None"/> once anything else happens.</summary>
	public KioskRefusal LastRefusal { get; private set; }

	/// <summary>The kind <see cref="LastRefusal"/> was for.</summary>
	public byte RefusedKind { get; private set; }

	/// <summary>A kind's name as players say it: <c>4x4</c>, <c>helicopter</c>.</summary>
	public static string NameOf(byte kind) => kind < Names.Length ? Names[kind] : $"#{kind}";

	public void Open()
	{
		IsOpen = true;
		Awaiting = false;
		LastRefusal = KioskRefusal.None;
	}

	public void Close()
	{
		IsOpen = false;
		Awaiting = false;
	}

	/// <summary>
	/// The panel's keys: <c>1</c> a 4x4, <c>2</c> a helicopter, <c>E</c> or Escape closes.
	/// The widget also closes on the <c>use</c> action, whatever key it is bound to, as the
	/// locker's does.
	/// </summary>
	public static KioskAction ActionOf(Key key) => key switch
	{
		Key.Key1 or Key.Kp1 => KioskAction.Ask4x4,
		Key.Key2 or Key.Kp2 => KioskAction.AskHelicopter,
		Key.E or Key.Escape => KioskAction.Close,
		_ => KioskAction.None,
	};

	/// <summary>The kind an ask action is for.</summary>
	public static byte KindOf(KioskAction action) =>
		action == KioskAction.AskHelicopter ? (byte)CrewVehicleKind.Helicopter : (byte)CrewVehicleKind.FourByFour;

	/// <summary>Whether an action would do anything now: what the widget greys out.</summary>
	public bool IsEnabled(KioskAction action) => action switch
	{
		KioskAction.Ask4x4 or KioskAction.AskHelicopter => IsOpen && !Awaiting,
		KioskAction.Close => IsOpen,
		_ => false,
	};

	/// <summary>Does what a key or a button asks, if it is enabled, and says what the panel has to do next.</summary>
	public KioskOutcome Apply(KioskAction action)
	{
		if (!IsEnabled(action))
		{
			return KioskOutcome.None;
		}

		if (action == KioskAction.Close)
		{
			Close();
			return KioskOutcome.Close;
		}

		AskedKind = KindOf(action);
		Awaiting = true;
		LastRefusal = KioskRefusal.None;
		return KioskOutcome.Ask;
	}

	/// <summary>
	/// The kiosk's answer (<c>ServerKioskResult</c>): taken closes the panel — the vehicle
	/// is on its pad; a refusal goes in the footer and leaves the panel open, the keys
	/// live again. An answer to nothing — a panel closed and opened again while one was on
	/// its way — is ignored.
	/// </summary>
	public KioskOutcome Answer(KioskRefusal refusal)
	{
		if (!IsOpen || !Awaiting)
		{
			return KioskOutcome.None;
		}

		Awaiting = false;
		if (refusal == KioskRefusal.None)
		{
			Close();
			return KioskOutcome.Close;
		}

		LastRefusal = refusal;
		RefusedKind = AskedKind;
		return KioskOutcome.Edited;
	}

	/// <summary>
	/// Whether the panel is still wanted (§8.5): while the player is alive on the ground
	/// force, on foot, in the kiosk's reach; it closes for death, a side change, the role
	/// menu, a seat, leaving the reach and the round's end.
	/// </summary>
	public bool StillWanted(in KioskMenuSituation situation) =>
		IsOpen && situation.Alive && situation.GroundForce && !situation.AwaitingRole && !situation.RoundEnded
		&& !situation.Seated && KioskRules.InReach(situation.KioskDistance);

	// ---- read-outs -------------------------------------------------------------------

	public const string Title = "VEHICLE KIOSK";

	/// <summary>A kind's row: <c>[1] 4x4 — ready</c>, <c>[2] helicopter — ready in 1:30</c>.</summary>
	public static string RowLabel(byte kind, in KioskLine line) => $"[{kind + 1}] {NameOf(kind)} — {Status(line)}";

	/// <summary>A kind's status on its row: <c>ready</c>, <c>out</c>, <c>ready in 0:42</c>, <c>not offered here</c>.</summary>
	public static string Status(in KioskLine line) => line.Status switch
	{
		KioskStatus.Ready => "ready",
		KioskStatus.Out => "out",
		KioskStatus.Counting => $"ready in {Clock(line.SecondsLeft)}",
		_ => "not offered here",
	};

	/// <summary>
	/// The panel's last line: the answer being waited for, the refusal the kiosk gave —
	/// explained against the kind's line as it stands now, so a <c>not_ready</c> counts
	/// down — or nothing.
	/// </summary>
	public string StatusLine(ReadOnlySpan<KioskLine> lines)
	{
		if (Awaiting)
		{
			return $"asking the kiosk for a {NameOf(AskedKind)}…";
		}

		if (LastRefusal == KioskRefusal.None)
		{
			return string.Empty;
		}

		KioskLine line = RefusedKind < lines.Length ? lines[RefusedKind] : KioskLine.None;
		return $"refused ({KioskRules.CodeOf(LastRefusal)}): {Explain(LastRefusal, line)}";
	}

	/// <summary>What a refusal means to the person who was refused it; <paramref name="line"/> is the kind's now.</summary>
	public static string Explain(KioskRefusal refusal, in KioskLine line) => refusal switch
	{
		KioskRefusal.Dead => "you are not on the field",
		KioskRefusal.NotGround => "the kiosk is the ground force's",
		KioskRefusal.NotAtKiosk => "too far from the kiosk",
		KioskRefusal.Seated => "get out of the vehicle first",
		KioskRefusal.Mounted => "get off the gun first",
		KioskRefusal.Carrying => "put down what you are carrying first",
		KioskRefusal.Piloting => "you are flying a drone",
		KioskRefusal.Invalid => "this kiosk does not offer it",
		KioskRefusal.Out => "one is out already: the next is ready a while after it is lost",
		KioskRefusal.NotReady => line.Status switch
		{
			KioskStatus.Counting => $"ready in {Clock(line.SecondsLeft)}",
			KioskStatus.Ready => "ready now",
			_ => "not ready",
		},
		KioskRefusal.PadBlocked => "something is on its pad",
		KioskRefusal.TooSoon => "one request a quarter-second; ask again",
		_ => string.Empty,
	};

	/// <summary>The foot of the panel.</summary>
	public const string KeysLine = "[1] 4x4   [2] helicopter   [E] close";

	/// <summary>Whole seconds as the HUD's clock says them: <c>0:42</c>, <c>1:30</c>.</summary>
	public static string Clock(int seconds)
	{
		int whole = Math.Max(seconds, 0);
		return $"{whole / 60}:{whole % 60:00}";
	}
}

/// <summary>
/// What the ground force's HUD and the debug HUD say about the kiosk
/// (docs/GROUND_VEHICLES.md §8.5), from the vehicle message's header.
/// </summary>
public static class KioskHud
{
	/// <summary>
	/// The line under the round's: <c>4x4 ready · helicopter 0:42</c> — each kind the
	/// kiosk offers, <c>out</c> while one is on the field; nothing on a map that offers none.
	/// </summary>
	public static string Line(ReadOnlySpan<KioskLine> lines) => Join(lines, offered: true);

	/// <summary>
	/// The debug HUD's part of the <c>vehicles</c> row: every kind, <c>off</c> for one the
	/// kiosk does not offer — a pad that failed its check, or no kiosk at all.
	/// </summary>
	public static string DebugLine(ReadOnlySpan<KioskLine> lines) => Join(lines, offered: false);

	private static string Join(ReadOnlySpan<KioskLine> lines, bool offered)
	{
		string joined = string.Empty;
		for (int kind = 0; kind < lines.Length && kind < KioskMenu.Kinds; kind++)
		{
			KioskLine line = lines[kind];
			if (offered && line.Status == KioskStatus.Disabled)
			{
				continue;
			}

			string word = line.Status switch
			{
				KioskStatus.Ready => "ready",
				KioskStatus.Out => "out",
				KioskStatus.Counting => KioskMenu.Clock(line.SecondsLeft),
				_ => "off",
			};

			string part = $"{KioskMenu.NameOf((byte)kind)} {word}";
			joined = joined.Length == 0 ? part : $"{joined} · {part}";
		}

		return joined;
	}
}
