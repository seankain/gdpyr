namespace Gdpyr.Sim;

/// <summary>
/// Why a kit asked for at a weapon locker was not taken (docs/LOADOUT.md §6.2). A
/// protocol constant: <c>ServerKitResult</c> carries it as a byte, and the agent API's
/// <c>loadout</c> op names it (<see cref="KitRequest.CodeOf"/>).
/// </summary>
public enum KitRefusal : byte
{
	/// <summary>Not refused: the kit was taken.</summary>
	None = 0,

	/// <summary>Not a living ground-force player: dead, a strategist, or still choosing a side.</summary>
	Dead = 1,

	/// <summary>No locker's footprint within its reach and the latency slack.</summary>
	NotAtLocker = 2,

	/// <summary>Behind a heavy gun.</summary>
	Mounted = 3,

	/// <summary>A heavy gun or an ammunition can on their back.</summary>
	Carrying = 4,

	/// <summary>A kit <see cref="Loadout.Sanitize"/> would change: refused rather than trimmed.</summary>
	Invalid = 5,

	/// <summary>Another kit was taken less than <see cref="KitRequest.MinIntervalTicks"/> ago.</summary>
	TooSoon = 6,
}

/// <summary>What the server knows about a player asking for a kit at a locker.</summary>
public readonly struct KitSituation
{
	/// <summary>A living ground-force player (<c>PlayerCombat.IsAlive</c>, which a strategist never is).</summary>
	public readonly bool Alive;

	public readonly bool Mounted;

	public readonly CarryKind Carrying;

	/// <summary>Flat distance from their feet to the nearest locker's footprint; infinite on a map without one.</summary>
	public readonly float LockerDistance;

	/// <summary>The kit asked for is one <see cref="Loadout.IsValid"/> accepts as it stands.</summary>
	public readonly bool ValidKit;

	public readonly uint Tick;

	/// <summary>The tick this player last took a kit at a locker; 0 for never.</summary>
	public readonly uint LastKitTick;

	public KitSituation(bool alive, bool mounted, CarryKind carrying, float lockerDistance, bool validKit, uint tick,
		uint lastKitTick)
	{
		Alive = alive;
		Mounted = mounted;
		Carrying = carrying;
		LockerDistance = lockerDistance;
		ValidKit = validKit;
		Tick = tick;
		LastKitTick = lastKitTick;
	}
}

/// <summary>
/// The one rule for taking a kit at a weapon locker (docs/LOADOUT.md L-D9, §5.3).
///
/// A person's panel, a bot's locker run and a policy's <c>loadout</c> op all ask
/// through it, so all three are held to the same distance, the same hands and the same
/// pace. The kit is a request beside the input frames rather than a key in them, which
/// is what lets a panel choose it without being recorded input; and a request is
/// something the server can refuse and say why.
///
/// Engine-free, as every rule a refusal can come from must be to be answered by a test.
/// </summary>
public static class KitRequest
{
	/// <summary>
	/// Reach beyond <see cref="SimConfig.LockerReachMeters"/> the server allows a
	/// request: the client saw itself at the locker a round trip ago, and a few tenths of
	/// a second of walking away should not turn that into a refusal.
	/// </summary>
	public const float SlackMeters = 0.5f;

	/// <summary>
	/// Ticks between two kits taken by one player: a quarter-second, so a held key or a
	/// script sending every tick cannot make the server re-equip every tick.
	/// </summary>
	public const int MinIntervalTicks = SimConfig.TickRate / 4;

	/// <summary>Each refusal as the agent API and the log spell it, by <see cref="KitRefusal"/>.</summary>
	private static readonly string[] Codes =
	{
		"applied", "dead", "not_at_locker", "mounted", "carrying", "invalid", "too_soon",
	};

	/// <summary>
	/// Whether a kit may be taken, and if not, why. A request that could never be
	/// honoured is refused as such whatever the asker is doing; then the asker's body,
	/// then where it stands, and last the pace, which only a kit that would otherwise
	/// have been taken is held to.
	/// </summary>
	public static KitRefusal Check(in KitSituation situation)
	{
		if (!situation.ValidKit)
		{
			return KitRefusal.Invalid;
		}

		if (!situation.Alive)
		{
			return KitRefusal.Dead;
		}

		if (situation.Mounted)
		{
			return KitRefusal.Mounted;
		}

		if (situation.Carrying != CarryKind.None)
		{
			return KitRefusal.Carrying;
		}

		if (!(situation.LockerDistance <= SimConfig.LockerReachMeters + SlackMeters))
		{
			return KitRefusal.NotAtLocker;
		}

		if (situation.LastKitTick != 0 && situation.Tick >= situation.LastKitTick
			&& situation.Tick - situation.LastKitTick < (uint)MinIntervalTicks)
		{
			return KitRefusal.TooSoon;
		}

		return KitRefusal.None;
	}

	/// <summary>A refusal's code, or <c>applied</c> for none.</summary>
	public static string CodeOf(KitRefusal refusal) =>
		(byte)refusal < Codes.Length ? Codes[(byte)refusal] : "unknown";

	/// <summary>A refusal read off the wire; false for a byte this build has no refusal for.</summary>
	public static bool TryRead(byte value, out KitRefusal refusal)
	{
		refusal = (KitRefusal)value;
		return value < Codes.Length;
	}
}
