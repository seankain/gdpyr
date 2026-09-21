using Godot;

namespace Gdpyr.Net;

/// <summary>
/// The one place the transport is chosen. Everything above this file talks to
/// <c>MultiplayerPeer</c>, so swapping ENet for a Steam peer in M9 is a change
/// here and nowhere else (docs/IMPLEMENTATION_PLAN.md §2).
/// </summary>
public static class TransportFactory
{
	/// <summary>Maximum simultaneous clients: 6 Ground Force + 2 Strategists, with headroom.</summary>
	public const int MaxClients = 16;

	/// <summary>
	/// Binds a listening peer. Returns the ENet error unchanged — the caller decides
	/// whether a failure is fatal.
	/// </summary>
	public static Error CreateServer(int port, out MultiplayerPeer peer, int maxClients = MaxClients)
	{
		var enet = new ENetMultiplayerPeer();
		Error error = enet.CreateServer(port, maxClients);
		peer = error == Error.Ok ? enet : null;
		return error;
	}

	/// <summary>
	/// Starts connecting to <paramref name="host"/>. ENet is asynchronous: <c>Ok</c>
	/// here means the socket was created, not that the server answered. Wait for
	/// <c>MultiplayerApi.ConnectedToServer</c> / <c>ConnectionFailed</c> for that.
	/// </summary>
	public static Error CreateClient(string host, int port, out MultiplayerPeer peer)
	{
		var enet = new ENetMultiplayerPeer();
		Error error = enet.CreateClient(host, port);
		peer = error == Error.Ok ? enet : null;
		return error;
	}
}
