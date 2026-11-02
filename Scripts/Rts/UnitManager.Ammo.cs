using System;
using System.Collections.Generic;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// Ammunition on the wire (docs/NETCODE.md §10.7): what each of the strategist side's
/// units has left, for the selection card (<see cref="Ammunition"/>).
///
/// It goes to strategist peers only, as <c>ServerAmmoState</c>, and never into the unit
/// snapshot every ground client receives: a tank that has fired its last round is
/// something the ground force finds out by being missed by nothing, not by reading a
/// packet. Sent as micro-autonomy is: whole, at the barracks report's rate, to every
/// strategist peer that has not been sent the current revision — bumped by every round
/// fired and every refill — so a peer that takes the chair has it within 0.2 s.
/// </summary>
public partial class UnitManager
{
	/// <summary>Bumped by every change; a strategist peer is sent the state when what it was last sent is older.</summary>
	private uint _ammoRevision = 1;

	private byte[] _ammoPayload;
	private uint _ammoPayloadRevision;

	/// <summary>The revision each strategist peer was last sent. A peer that leaves the chair is forgotten.</summary>
	private readonly Dictionary<int, uint> _ammoSent = new();

	private readonly UnitAmmoRecord[] _ammoRecords = new UnitAmmoRecord[AmmoStateCodec.MaxRecords];

	/// <summary>A new round: every unit is new and full, and every peer is sent that.</summary>
	private void ResetAmmo()
	{
		_ammoSent.Clear();
		_ammoRevision++;
	}

	/// <summary>
	/// The state to every strategist peer that has not been sent this revision, whole.
	/// Encoded once a revision. Nothing for a ground peer, which is forgotten so that
	/// taking the chair later sends it everything.
	/// </summary>
	private void SendAmmoState()
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
				_ammoSent.Remove(peerId);
				continue;
			}

			if (_ammoSent.TryGetValue(peerId, out uint sent) && sent == _ammoRevision)
			{
				continue;
			}

			byte[] payload = AmmoPayload();
			RpcId(peerId, MethodName.ServerAmmoState, payload);
			NetworkManager.Instance?.Stats.RecordSent(payload.Length);
			_ammoSent[peerId] = _ammoRevision;
		}
	}

	/// <summary>Every living strategist unit short of a full load.</summary>
	private byte[] AmmoPayload()
	{
		if (_ammoPayload != null && _ammoPayloadRevision == _ammoRevision)
		{
			return _ammoPayload;
		}

		int count = 0;
		for (int i = 0; i < _ordered.Count && count < _ammoRecords.Length; i++)
		{
			Unit unit = _ordered[i];
			if (unit.IsAlive && unit.Team == Team.Strategist && !Ammunition.IsFull(unit.Rounds, unit.FullRounds))
			{
				_ammoRecords[count++] = new UnitAmmoRecord { UnitId = unit.UnitId, Rounds = (ushort)unit.Rounds };
			}
		}

		_ammoPayload = AmmoStateCodec.Encode(_ammoRecords.AsSpan(0, count));
		_ammoPayloadRevision = _ammoRevision;
		return _ammoPayload;
	}

	/// <summary>Server → strategist clients: the side's ammunition, whole. Every unit not listed is full.</summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerAmmoState(byte[] payload)
	{
		NetworkManager.Instance?.Stats.RecordReceived(payload?.Length ?? 0);
		if (!AmmoStateCodec.TryDecode(payload, _ammoRecords, out int count))
		{
			return;
		}

		for (int i = 0; i < _ordered.Count; i++)
		{
			if (_ordered[i].Team == Team.Strategist)
			{
				_ordered[i].Rounds = _ordered[i].FullRounds;
			}
		}

		for (int i = 0; i < count; i++)
		{
			if (Find(_ammoRecords[i].UnitId) is { } unit && Ammunition.IsLimited(unit.FullRounds))
			{
				unit.Rounds = Math.Min(_ammoRecords[i].Rounds, unit.FullRounds);
			}
		}
	}
}
