using System.Collections.Generic;
using Godot;

namespace Gdpyr.Net;

/// <summary>
/// Looks for servers on this network and keeps a list of what answered
/// (docs/LAN.md §4).
///
/// One broadcast per port in the discovery range per refresh, and a row per reply.
/// There is no registry and nothing to sign up to: a server that is running
/// answers, a server that is not does not, and a row that stops answering ages out
/// of the list on its own.
///
/// It finds what a broadcast reaches — the same subnet — which is the room and not
/// the internet. Anything further away is typed in by address, and the menu says
/// so rather than pretending the list is exhaustive.
/// </summary>
public partial class ServerBrowser : Node
{
	/// <summary>How long a row lives without being heard from again. Two refreshes plus slack.</summary>
	public const float EntryLifetimeSeconds = 6f;

	/// <summary>Replies drained per frame. A LAN answers in a burst; this bounds a flood.</summary>
	private const int MaxRepliesPerFrame = 64;

	private readonly PacketPeerUdp _socket = new();
	private readonly List<Entry> _entries = new();

	private float _age;
	private bool _open;

	private sealed class Entry
	{
		public ServerInfo Info;
		public float LastHeard;
	}

	/// <summary>Queries sent since this browser opened. The menu shows nothing until the first one.</summary>
	public int Refreshes { get; private set; }

	public int Count => _entries.Count;

	public ServerInfo this[int index] => _entries[index].Info;

	public override void _Ready()
	{
		ProcessMode = ProcessModeEnum.Always;

		// Port 0 asks the operating system for a free one: this socket only ever
		// receives replies to datagrams it sent, so nothing has to be able to find it.
		Error error = _socket.Bind(0, "*", DiscoveryCodec.MaxReplyLength * 32);
		if (error != Error.Ok)
		{
			GD.PushWarning($"[browser] could not open a discovery socket: {error};"
				+ " servers will have to be joined by address");
			return;
		}

		_socket.SetBroadcastEnabled(true);
		_open = true;
	}

	public override void _ExitTree() => _socket.Close();

	public override void _Process(double delta)
	{
		_age += (float)delta;
		Drain();
		Expire();
	}

	/// <summary>
	/// Broadcasts a query on every port in the discovery range. Rows already found
	/// are kept: a refresh that emptied the list for half a second would make the one
	/// button a player presses feel broken.
	/// </summary>
	public void Refresh()
	{
		if (!_open)
		{
			return;
		}

		byte[] query = DiscoveryCodec.EncodeQuery();

		for (int offset = 0; offset < DiscoveryCodec.PortSpan; offset++)
		{
			int port = DiscoveryCodec.PortBase + offset;

			// The broadcast address reaches the room; loopback reaches a host running
			// on this machine, which a broadcast is not guaranteed to be delivered to
			// and which is how most of this gets tested (docs/LAN.md §7).
			Send(query, "255.255.255.255", port);
			Send(query, "127.0.0.1", port);
		}

		Refreshes++;
	}

	/// <summary>Forgets every row, so the next refresh starts from nothing.</summary>
	public void Clear() => _entries.Clear();

	private void Send(byte[] query, string address, int port)
	{
		if (_socket.SetDestAddress(address, port) == Error.Ok)
		{
			_socket.PutPacket(query);
		}
	}

	private void Drain()
	{
		for (int i = 0; i < MaxRepliesPerFrame && _socket.GetAvailablePacketCount() > 0; i++)
		{
			byte[] packet = _socket.GetPacket();
			string address = _socket.GetPacketIP();

			if (!DiscoveryCodec.TryDecodeReply(packet, out ServerInfo info) || string.IsNullOrEmpty(address))
			{
				continue;
			}

			// The address is the one the datagram came from, never one the server
			// claimed: a host behind a NAT or with three interfaces does not know which
			// of its addresses this client can reach, and this one demonstrably works.
			info.Address = address;
			if (string.IsNullOrWhiteSpace(info.Name))
			{
				info.Name = address;
			}

			Record(info);
		}
	}

	/// <summary>
	/// Adds a row, or refreshes the one already there. Identity is the endpoint: one
	/// machine can host several servers, and a server that renames itself is still
	/// the same server.
	/// </summary>
	private void Record(in ServerInfo info)
	{
		for (int i = 0; i < _entries.Count; i++)
		{
			Entry entry = _entries[i];
			if (entry.Info.Address == info.Address && entry.Info.GamePort == info.GamePort)
			{
				entry.Info = info;
				entry.LastHeard = _age;
				return;
			}
		}

		_entries.Add(new Entry { Info = info, LastHeard = _age });

		// Sorted by endpoint — a name is the host's to change, an address is not — so
		// that the list does not reorder itself under the cursor as replies arrive in
		// whatever order the network delivered them.
		_entries.Sort(static (a, b) => string.CompareOrdinal(a.Info.Endpoint, b.Info.Endpoint));
	}

	private void Expire()
	{
		for (int i = _entries.Count - 1; i >= 0; i--)
		{
			if (_age - _entries[i].LastHeard > EntryLifetimeSeconds)
			{
				_entries.RemoveAt(i);
			}
		}
	}
}
