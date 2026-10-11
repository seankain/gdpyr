using System;
using Gdpyr.Core;
using Gdpyr.Match;
using Gdpyr.Net;
using Gdpyr.Sim;
using Gdpyr.Sim.Agent;
using Godot;

namespace Gdpyr.Rts;

/// <summary>
/// Units trained by structures (docs/NETCODE.md §10.8): a finished sniper tower
/// trains snipers and a finished pillbox heavy gunners, each from a queue of its
/// own (<see cref="Structure.Queue"/>), as a barracks trains from its.
///
/// Everything here is a barracks' production rule pointed at a structure: a unit is
/// charged for when it is queued, cancelling the last one refunds it, one comes out
/// at a time, and it walks out of the door to a rally point the strategist can move.
/// Two things differ, because a structure is not a barracks. Its door is worked out
/// from where it stands (<see cref="StructureTraining"/>) rather than authored; and
/// it can be knocked down, which refunds whatever was still on its queue — the
/// strategist paid for units, and gets either the units or the points back.
///
/// What a client is told is one more reliable message, sent at the structure report
/// rate while a queue is moving: how many are queued and how far the head is. What
/// the head is needs no saying — a structure trains one kind of unit.
/// </summary>
public partial class UnitManager
{
	/// <summary>Bytes counted for one training-state RPC, for the bandwidth HUD.</summary>
	private const int TrainingStateBytes = 6;

	/// <summary>Bytes counted for a train or cancel request; a rally request carries a point as well.</summary>
	private const int TrainRequestBytes = 5;

	private const int TrainingRallyBytes = 17;

	/// <summary>How far back from a structure fresh units come out when its side has no barracks to call home.</summary>
	private const float HomelessDoorMeters = 10f;

	private readonly Footprint[] _otherFootprints = new Footprint[SimConfig.MaxStructures];
	private readonly byte[] _sentQueued = new byte[SimConfig.MaxStructures];
	private readonly byte[] _sentTraining = new byte[SimConfig.MaxStructures];

	/// <summary>Units on every structure's training queue. Server-side, and paid for, as a barracks' are.</summary>
	private int QueuedInStructures
	{
		get
		{
			int queued = 0;
			for (int i = 0; i < _structures.Length; i++)
			{
				if (_structures[i] is { IsDestroyed: false } structure)
				{
					queued += structure.Queue.Count;
				}
			}
			return queued;
		}
	}

	// ---- requests ---------------------------------------------------------------

	/// <summary>Asks the server to put one of what the structure in <paramref name="slot"/> trains on its queue.</summary>
	public void RequestTrain(int slot)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			RpcId(1, MethodName.ClientTrain, slot);
			net.Stats.RecordSent(TrainRequestBytes);
			return;
		}

		ApplyTrain(net?.LocalPeerId ?? 1, slot);
	}

	/// <summary>Asks the server to take the last unit off a structure's queue and refund it.</summary>
	public void RequestCancelTrain(int slot)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			RpcId(1, MethodName.ClientCancelTrain, slot);
			net.Stats.RecordSent(TrainRequestBytes);
			return;
		}

		ApplyCancelTrain(net?.LocalPeerId ?? 1, slot);
	}

	/// <summary>Asks the server to move where a structure's fresh units walk to.</summary>
	public void RequestTrainingRally(int slot, Vector3 point)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net != null && net.IsClient)
		{
			RpcId(1, MethodName.ClientTrainingRally, slot, point);
			net.Stats.RecordSent(TrainingRallyBytes);
			return;
		}

		ApplyTrainingRally(net?.LocalPeerId ?? 1, slot, point);
	}

	/// <summary>Client -> server, reliable: a queued unit is paid for, so it is a decision and not a sample.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientTrain(int slot)
	{
		if (NetworkManager.Instance is { IsServer: true })
		{
			ApplyTrain(Multiplayer.GetRemoteSenderId(), slot);
		}
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientCancelTrain(int slot)
	{
		if (NetworkManager.Instance is { IsServer: true })
		{
			ApplyCancelTrain(Multiplayer.GetRemoteSenderId(), slot);
		}
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientTrainingRally(int slot, Vector3 point)
	{
		if (NetworkManager.Instance is { IsServer: true })
		{
			ApplyTrainingRally(Multiplayer.GetRemoteSenderId(), slot, point);
		}
	}

	/// <summary>
	/// Server-side: queues a unit at a structure on behalf of a peer with no client —
	/// a strategist policy (docs/AGENT_API.md §7.4) — through the same checks a
	/// person's request gets. True when it was charged for and queued.
	/// </summary>
	public bool ServerTrain(int peerId, int slot) =>
		NetworkManager.Instance is not { IsClient: true } && ApplyTrain(peerId, slot);

	/// <summary>Server-side: the parity half of <see cref="RequestCancelTrain"/>.</summary>
	public void ServerCancelTrain(int peerId, int slot)
	{
		if (NetworkManager.Instance is not { IsClient: true })
		{
			ApplyCancelTrain(peerId, slot);
		}
	}

	/// <summary>Server-side: the parity half of <see cref="RequestTrainingRally"/>.</summary>
	public void ServerSetTrainingRally(int peerId, int slot, Vector3 point)
	{
		if (NetworkManager.Instance is not { IsClient: true })
		{
			ApplyTrainingRally(peerId, slot, point);
		}
	}

	private bool ApplyTrain(int senderPeerId, int slot)
	{
		Structure structure = ValidateTrainer(senderPeerId, slot);
		UnitDefinition definition = structure != null ? UnitCatalog.Definition(structure.TrainsUnit) : null;
		ResourceLedger ledger = CombatManager.Instance?.Match.Strategist;
		if (definition == null || ledger == null)
		{
			RejectedOrders++;
			return false;
		}

		return structure.Queue.TryEnqueue(structure.TrainsUnit, definition.Cost, definition.BuildTicks, ledger);
	}

	private void ApplyCancelTrain(int senderPeerId, int slot) =>
		ValidateTrainer(senderPeerId, slot)?.Queue.CancelLast(CombatManager.Instance?.Match.Strategist);

	private void ApplyTrainingRally(int senderPeerId, int slot, Vector3 point)
	{
		Structure structure = ValidateTrainer(senderPeerId, slot);
		if (structure == null || !float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
		{
			RejectedOrders++;
			return;
		}

		structure.RallyPoint = point;
		structure.HasRally = true;
	}

	/// <summary>
	/// The structure a training request names, if the sender may use it: the sender is
	/// a strategist, and it is their side's, standing, finished, and trains something
	/// this build has. Every one of those is a number a client chose.
	/// </summary>
	private Structure ValidateTrainer(int senderPeerId, int slot)
	{
		Team team = CombatManager.Instance?.TeamOf(senderPeerId) ?? Team.GroundForce;
		Structure structure = StructureAt(slot);
		return team == Team.Strategist && structure != null && !structure.IsDestroyed && structure.IsBuilt
			&& structure.Team == team && structure.Trains && UnitCatalog.IsBuildable(structure.TrainsUnit)
				? structure
				: null;
	}

	// ---- server: the tick ---------------------------------------------------------

	/// <summary>
	/// One tick of a finished structure's queue: a unit out of its door when the head
	/// is done, sent to the rally point. Called from the structure pass, for finished
	/// structures only — a site trains nothing, and its queue is empty because a site
	/// cannot be queued at.
	/// </summary>
	private void ServerTraining(Structure structure, uint tick)
	{
		if (!structure.Trains || !structure.Queue.Tick(tick, out byte definitionId))
		{
			return;
		}

		Footprint footprint = structure.Footprint;
		Vector3 door = TrainingDoor(structure);
		Unit unit = SpawnUnit(definitionId, StructureTraining.Slot(footprint, door, structure.Queue.Produced),
			structure.Team);
		if (unit == null)
		{
			// The field is full: the points back, as a barracks gives them.
			CombatManager.Instance?.Match.Strategist.Refund(UnitCatalog.CostOf(definitionId));
			GD.PushWarning($"[rts] unit pool full ({SimConfig.MaxUnits}); refunded a {UnitCatalog.NameOf(definitionId)}"
				+ $" from {StructureCatalog.NameOf(structure.Kind)} {structure.Slot}");
			return;
		}

		unit.Order = new UnitOrder
		{
			Kind = OrderKind.Move,
			Target = structure.HasRally ? structure.RallyPoint : StructureTraining.DefaultRally(footprint, door),
			Anchor = unit.GlobalPosition,
			TargetOwnerId = OwnerId.None,
		};
		UnitsProduced++;

		// The event's barracks slot names a structure as -(slot + 1), so a reader that
		// only knows barracks sees "not a barracks" and one that knows reads the slot.
		AgentEventBus.Emit(AgentEventKind.UnitBuilt, tick, unit.UnitId, definitionId, TrainedBy(structure.Slot),
			unit.GlobalPosition.X, unit.GlobalPosition.Z);
	}

	/// <summary>The barracks field of a unit-built event for a unit a structure trained.</summary>
	public static int TrainedBy(int structureSlot) => -1 - structureSlot;

	/// <summary>
	/// Where a structure's units come out: the wall facing the nearest barracks on its
	/// side, or the next one round that no other structure is in front of.
	/// </summary>
	private Vector3 TrainingDoor(Structure structure)
	{
		Footprint footprint = structure.Footprint;
		Vector3 at = structure.Pose.Base;
		Vector3 home = at - (footprint.AxisZ * HomelessDoorMeters);
		float nearest = float.MaxValue;
		for (int i = 0; i < _barracks.Count; i++)
		{
			Barracks barracks = _barracks[i];
			float distance = at.DistanceSquaredTo(barracks.SpawnPosition);
			if (barracks.Team == structure.Team && distance < nearest)
			{
				nearest = distance;
				home = barracks.SpawnPosition;
			}
		}

		int others = 0;
		for (int i = 0; i < _structures.Length; i++)
		{
			if (_structures[i] is { IsDestroyed: false } other && other != structure)
			{
				_otherFootprints[others++] = other.Footprint;
			}
		}

		return StructureTraining.Door(footprint, home, _otherFootprints.AsSpan(0, others));
	}

	/// <summary>
	/// Hands back everything still on a knocked-down structure's queue. The strategist
	/// paid for units; a pillbox that is rubble owes them the points.
	/// </summary>
	private static void RefundTraining(Structure structure)
	{
		ResourceLedger ledger = CombatManager.Instance?.Match.Strategist;
		while (structure.Queue.CancelLast(ledger))
		{
		}
	}

	// ---- replication ----------------------------------------------------------------

	/// <summary>
	/// Each training structure's queue and progress, at the structure report rate,
	/// when either has changed since it was last sent. Nothing for a structure that
	/// trains nothing, and nothing for an idle one after the message that said so.
	/// </summary>
	private void BroadcastTrainingState(uint tick)
	{
		if (tick % StructureReportIntervalTicks != 0)
		{
			return;
		}

		int peers = Multiplayer.HasMultiplayerPeer() ? Multiplayer.GetPeers().Length : 0;
		for (int slot = 0; slot < _structures.Length; slot++)
		{
			Structure structure = _structures[slot];
			if (structure == null || !structure.Trains)
			{
				continue;
			}

			byte queued = (byte)Mathf.Min(structure.Queue.Count, byte.MaxValue);
			byte progress = ProgressByte(structure.Queue.Progress(tick));
			if (queued == _sentQueued[slot] && progress == _sentTraining[slot])
			{
				continue;
			}

			_sentQueued[slot] = queued;
			_sentTraining[slot] = progress;

			if (peers > 0)
			{
				Rpc(MethodName.ServerTrainingState, slot, queued, progress);
				NetworkManager.Instance?.Stats.RecordSent(TrainingStateBytes * peers);
			}
		}
	}

	/// <summary>A joining peer's copy of every queue that has something on it.</summary>
	private void SendTrainingTo(int peerId, uint tick)
	{
		for (int slot = 0; slot < _structures.Length; slot++)
		{
			Structure structure = _structures[slot];
			if (structure is { Trains: true } && structure.Queue.Count > 0)
			{
				RpcId(peerId, MethodName.ServerTrainingState, slot,
					(byte)Mathf.Min(structure.Queue.Count, byte.MaxValue), ProgressByte(structure.Queue.Progress(tick)));
			}
		}
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ServerTrainingState(int slot, byte queued, byte progress)
	{
		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.IsClient)
		{
			return;
		}

		net.Stats.RecordReceived(TrainingStateBytes);
		StructureAt(slot)?.ApplyRemoteTraining(queued, progress);
	}

	private static byte ProgressByte(float progress) =>
		(byte)Mathf.RoundToInt(Mathf.Clamp(progress, 0f, 1f) * byte.MaxValue);
}
