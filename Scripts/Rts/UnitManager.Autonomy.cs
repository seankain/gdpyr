using System;
using System.Collections.Generic;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// Micro-autonomy (docs/TACTICS.md §5.9): whether each of the strategist side's units
/// uses the tactics of docs/TACTICS.md §5 on its own, and what new units start with.
/// The strategist's to set, in game, through the same checks an order gets; the
/// unit's planner observes it as a fact (<c>UnitFact.MicroAutonomy</c>).
///
/// It goes to strategist peers only, as <c>ServerAutonomyState</c>, and never into the
/// unit snapshot every ground client receives: a setting is an order, and orders are
/// not on the wire to the other side (docs/NETCODE.md §6.3). A new round starts from
/// the resolved default again, as a new round starts the commander fresh: one round's
/// choices must not leak into the next's, which is what made scenario seeds depend on
/// each other in docs/HTN_BOTS.md §5.3.
/// </summary>
public partial class UnitManager
{
	/// <summary>
	/// What the strategist side's new units start with. On the authority, the state;
	/// on a client, what the last <c>ServerAutonomyState</c> said.
	/// </summary>
	public MicroAutonomy AutonomyDefault { get; private set; } = MicroAutonomyNames.Default;

	/// <summary>What <see cref="AutonomyDefault"/> was resolved to at start: <c>--unit-autonomy</c>, else the game mode's.</summary>
	private MicroAutonomy _autonomyStart = MicroAutonomyNames.Default;

	private bool _autonomyResolved;

	/// <summary>Bumped by every change; a strategist peer is sent the state when what it was last sent is older.</summary>
	private uint _autonomyRevision = 1;

	private byte[] _autonomyPayload;
	private uint _autonomyPayloadRevision;

	/// <summary>The revision each strategist peer was last sent. A peer that leaves the chair is forgotten, so it is sent the whole state when it comes back.</summary>
	private readonly Dictionary<int, uint> _autonomySent = new();

	private readonly UnitAutonomyRecord[] _autonomyRecords = new UnitAutonomyRecord[AutonomyStateCodec.MaxRecords];

	// ---- requests -----------------------------------------------------------

	/// <summary>Sets the selection's micro-autonomy. Applied immediately on the authority; a client asks.</summary>
	public void RequestAutonomy(int[] unitIds, MicroAutonomy mode)
	{
		if (unitIds == null || unitIds.Length == 0)
		{
			return;
		}

		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			RpcId(1, MethodName.ClientSetAutonomy, unitIds, (byte)mode);
			net.Stats.RecordSent(5 + (unitIds.Length * 4));
			return;
		}

		ApplyAutonomy(net?.LocalPeerId ?? 1, unitIds, (byte)mode);
	}

	/// <summary>Sets what the side's new units start with.</summary>
	public void RequestAutonomyDefault(MicroAutonomy mode)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			RpcId(1, MethodName.ClientAutonomyDefault, (byte)mode);
			net.Stats.RecordSent(5);
			return;
		}

		ApplyAutonomyDefault(net?.LocalPeerId ?? 1, (byte)mode);
	}

	/// <summary>An agent seat's <c>autonomy</c> command (docs/AGENT_API.md §7.4): the checks a person's request gets.</summary>
	public void ServerSetAutonomy(int senderPeerId, ReadOnlySpan<int> unitIds, MicroAutonomy mode) =>
		ApplyAutonomy(senderPeerId, unitIds, (byte)mode);

	/// <summary>An agent seat's <c>autonomy_default</c> command.</summary>
	public void ServerAutonomyDefault(int senderPeerId, MicroAutonomy mode) =>
		ApplyAutonomyDefault(senderPeerId, (byte)mode);

	/// <summary>
	/// Sets a unit a scenario placed (docs/AGENT_API.md §9.1, a spawn's <c>autonomy</c>).
	/// No owner to check, as <see cref="ServerPlaceUnit"/> has none: the caller is the
	/// agent socket. False for a unit that is not there.
	/// </summary>
	public bool ServerSetPlacedAutonomy(ushort unitId, MicroAutonomy mode)
	{
		if (NetworkManager.Instance is { IsClient: true } || Find(unitId) is not { IsAlive: true } unit)
		{
			return false;
		}

		SetAutonomy(unit, mode);
		return true;
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientSetAutonomy(int[] unitIds, byte mode)
	{
		if (NetworkManager.Instance is not { IsServer: true })
		{
			return;
		}

		NetworkManager.Instance.Stats.RecordReceived(5 + ((unitIds?.Length ?? 0) * 4));
		ApplyAutonomy(Multiplayer.GetRemoteSenderId(), unitIds, mode);
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientAutonomyDefault(byte mode)
	{
		if (NetworkManager.Instance is { IsServer: true })
		{
			ApplyAutonomyDefault(Multiplayer.GetRemoteSenderId(), mode);
		}
	}

	/// <summary>
	/// Validates and applies a setting, by <see cref="ApplyOrder"/>'s rules: the sender
	/// is a strategist, the mode exists, the sender's side owns each unit. What an
	/// honest client cannot send counts in <see cref="RejectedOrders"/>; a unit that
	/// died a moment ago is skipped. A rider is set too: it is the strategist's army
	/// still, and gets out with the mode it was given.
	/// </summary>
	private void ApplyAutonomy(int senderPeerId, ReadOnlySpan<int> unitIds, byte mode)
	{
		if (unitIds.Length == 0 || !MicroAutonomyNames.IsValid(mode))
		{
			RejectedOrders++;
			return;
		}

		Team senderTeam = CombatManager.Instance?.TeamOf(senderPeerId) ?? Team.GroundForce;
		if (senderTeam != Team.Strategist)
		{
			RejectedOrders++;
			return;
		}

		for (int i = 0; i < unitIds.Length; i++)
		{
			int raw = unitIds[i];
			if (raw <= 0 || raw > ushort.MaxValue)
			{
				RejectedOrders++;
				continue;
			}

			Unit unit = Find((ushort)raw);
			if (unit == null || !unit.IsAlive)
			{
				continue;
			}

			if (unit.Team != senderTeam)
			{
				RejectedOrders++;
				continue;
			}

			SetAutonomy(unit, (MicroAutonomy)mode);
		}
	}

	private void ApplyAutonomyDefault(int senderPeerId, byte mode)
	{
		if (!MicroAutonomyNames.IsValid(mode)
			|| (CombatManager.Instance?.TeamOf(senderPeerId) ?? Team.GroundForce) != Team.Strategist)
		{
			RejectedOrders++;
			return;
		}

		EnsureAutonomy();
		if (AutonomyDefault != (MicroAutonomy)mode)
		{
			AutonomyDefault = (MicroAutonomy)mode;
			_autonomyRevision++;
		}
	}

	private void SetAutonomy(Unit unit, MicroAutonomy mode)
	{
		if (unit.Autonomy != mode)
		{
			unit.Autonomy = mode;
			_autonomyRevision++;
		}
	}

	// ---- server: state ------------------------------------------------------

	/// <summary>
	/// Resolves the default once, on the authority: <c>--unit-autonomy</c>, else the game
	/// mode's, else off. Not latched before the round's game mode is there to read, so a
	/// unit placed that early cannot fix the default without it.
	/// </summary>
	private void EnsureAutonomy()
	{
		if (_autonomyResolved)
		{
			return;
		}

		GameModeDefinition gameMode = CombatManager.Instance?.GameMode;
		if (gameMode == null)
		{
			AutonomyDefault = MicroAutonomyNames.Resolve(Bootstrap.Options?.UnitAutonomy, null);
			return;
		}

		_autonomyResolved = true;
		_autonomyStart = MicroAutonomyNames.Resolve(Bootstrap.Options?.UnitAutonomy, gameMode.UnitAutonomy);
		AutonomyDefault = _autonomyStart;
		GD.Print($"[rts] new units start with micro-autonomy {MicroAutonomyNames.Name(AutonomyDefault)}"
			+ " (docs/TACTICS.md §5.9)");
	}

	/// <summary>A new round: the default as it was resolved, and every strategist told so.</summary>
	private void ResetAutonomy()
	{
		if (_autonomyResolved && AutonomyDefault != _autonomyStart)
		{
			AutonomyDefault = _autonomyStart;
		}

		_autonomyRevision++;
	}

	/// <summary>
	/// The state to every strategist peer that has not been sent this revision, whole;
	/// at the barracks report's rate, so a burst of clicks is one message. Encoded once
	/// a revision. Nothing for a ground peer, which is forgotten so that taking the
	/// chair later sends it everything.
	/// </summary>
	private void SendAutonomyState()
	{
		if (!Multiplayer.HasMultiplayerPeer() || CombatManager.Instance is not { } combat)
		{
			return;
		}

		int[] peers = Multiplayer.GetPeers();
		for (int i = 0; i < peers.Length; i++)
		{
			int peerId = peers[i];
			if (combat.TeamOf(peerId) != Team.Strategist)
			{
				_autonomySent.Remove(peerId);
				continue;
			}

			if (_autonomySent.TryGetValue(peerId, out uint sent) && sent == _autonomyRevision)
			{
				continue;
			}

			byte[] payload = AutonomyPayload();
			RpcId(peerId, MethodName.ServerAutonomyState, payload);
			NetworkManager.Instance?.Stats.RecordSent(payload.Length);
			_autonomySent[peerId] = _autonomyRevision;
		}
	}

	/// <summary>The default, and every living strategist unit whose mode is not it.</summary>
	private byte[] AutonomyPayload()
	{
		if (_autonomyPayload != null && _autonomyPayloadRevision == _autonomyRevision)
		{
			return _autonomyPayload;
		}

		int count = 0;
		for (int i = 0; i < _ordered.Count && count < _autonomyRecords.Length; i++)
		{
			Unit unit = _ordered[i];
			if (unit.IsAlive && unit.Team == Team.Strategist && unit.Autonomy != AutonomyDefault)
			{
				_autonomyRecords[count++] = new UnitAutonomyRecord { UnitId = unit.UnitId, Mode = unit.Autonomy };
			}
		}

		_autonomyPayload = AutonomyStateCodec.Encode(AutonomyDefault, _autonomyRecords.AsSpan(0, count));
		_autonomyPayloadRevision = _autonomyRevision;
		return _autonomyPayload;
	}

	// ---- client -------------------------------------------------------------

	/// <summary>Server → strategist clients: the side's micro-autonomy, whole. Every unit not listed is the default.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerAutonomyState(byte[] payload)
	{
		NetworkManager.Instance?.Stats.RecordReceived(payload?.Length ?? 0);
		if (!AutonomyStateCodec.TryDecode(payload, _autonomyRecords, out MicroAutonomy teamDefault, out int count))
		{
			return;
		}

		AutonomyDefault = teamDefault;
		for (int i = 0; i < _ordered.Count; i++)
		{
			if (_ordered[i].Team == Team.Strategist)
			{
				_ordered[i].Autonomy = teamDefault;
			}
		}

		for (int i = 0; i < count; i++)
		{
			if (Find(_autonomyRecords[i].UnitId) is { } unit)
			{
				unit.Autonomy = _autonomyRecords[i].Mode;
			}
		}
	}
}
