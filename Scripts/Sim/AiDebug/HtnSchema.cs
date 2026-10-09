using System;
using Gdpyr.Sim.Htn;

namespace Gdpyr.Sim.AiDebug;

/// <summary>
/// What each domain's facts and goals are called, and how a fact's byte reads
/// (docs/AI_DEBUG.md §3.4). The world state is one byte per fact (docs/HTN_BOTS.md
/// §3.7); this is the table that turns <c>Contact 2</c> back into <c>Contact=Visible</c>.
/// </summary>
public sealed class HtnSchema
{
	private readonly string[] _facts;
	private readonly Type[] _values;
	private readonly Type _goals;

	private HtnSchema(HtnDomainKind kind, Type facts, Type goals, params Type[] values)
	{
		Kind = kind;
		_facts = Enum.GetNames(facts);
		_values = values;
		_goals = goals;

		// The enums end in Count, which is not a fact.
		if (_facts.Length > 0 && _facts[^1] == "Count")
		{
			Array.Resize(ref _facts, _facts.Length - 1);
		}

		if (_values.Length != _facts.Length)
		{
			throw new InvalidOperationException($"{kind}: {_facts.Length} facts but {_values.Length} value types");
		}
	}

	public HtnDomainKind Kind { get; }

	public int FactCount => _facts.Length;

	public static readonly HtnSchema Ground = new(HtnDomainKind.Ground, typeof(GroundFact), typeof(GroundGoal),
		typeof(ContactLevel), // Contact
		typeof(Threat), // ThreatKind
		typeof(Arms), // Armed
		typeof(HealthBand), // Health
		typeof(OddsBand), // Odds
		typeof(bool), // AlliesNear
		typeof(GroundRole), // Role
		typeof(bool), // AtZone
		typeof(bool), // InsideDefences
		typeof(bool), // MagazineLow
		typeof(bool)); // Sweep

	public static readonly HtnSchema Unit = new(HtnDomainKind.Unit, typeof(UnitFact), typeof(UnitGoal),
		typeof(OrderKind), // Order
		typeof(UnitContact), // Contact
		typeof(HealthBand), // Health
		typeof(OddsBand), // Odds
		typeof(bool), // FriendEngaged
		typeof(SquadPhase), // SquadPhase
		typeof(bool), // InLeash
		typeof(UnitStance), // Stance
		typeof(bool), // UnderFire
		typeof(bool), // FriendNear
		typeof(bool), // Wounded
		typeof(bool), // Supply
		typeof(bool)); // Supplied

	public static readonly HtnSchema Squad = new(HtnDomainKind.Squad, typeof(CommandFact), typeof(CommandGoal),
		typeof(CommandTask), // Task
		typeof(bool), // FallingBack
		typeof(bool), // Depleted
		typeof(bool)); // Engaged

	/// <summary>The schema for a domain, or null for <see cref="HtnDomainKind.None"/>.</summary>
	public static HtnSchema For(HtnDomainKind kind) => kind switch
	{
		HtnDomainKind.Ground => Ground,
		HtnDomainKind.Unit => Unit,
		HtnDomainKind.Squad => Squad,
		_ => null,
	};

	public string FactName(int fact) => fact >= 0 && fact < _facts.Length ? _facts[fact] : $"fact{fact}";

	/// <summary>A fact's value as the domain's conditions spell it: <c>Visible</c>, <c>True</c>, <c>Outnumbered</c>.</summary>
	public string FactValue(int fact, byte value)
	{
		if (fact < 0 || fact >= _values.Length)
		{
			return value.ToString();
		}

		return Name(_values[fact], value);
	}

	/// <summary>A goal byte as its enum name: <c>Engage</c>, <c>HoldPost</c>, <c>Strike</c>.</summary>
	public string GoalName(byte goal) => Name(_goals, goal);

	/// <summary>The name of <paramref name="value"/> in <paramref name="type"/>, or the number when it has none.</summary>
	public static string Name(Type type, byte value)
	{
		if (type == typeof(bool))
		{
			// As the builders name their conditions: `AtZone=True`.
			return value != 0 ? "True" : "False";
		}

		if (type == null || !type.IsEnum)
		{
			return value.ToString();
		}

		return Enum.GetName(type, Enum.ToObject(type, value)) ?? value.ToString();
	}
}
