using Gdpyr.Core;
using Gdpyr.Level;
using Gdpyr.Net;
using Gdpyr.Sim.Level;
using Godot;

namespace Gdpyr.MapEditor;

/// <summary>
/// <b>Publish to server…</b> (docs/LEVEL_EDITOR.md §9.5, LE8): the saved map sent to a dedicated
/// server that was started with <c>--map-upload-token</c>, and, if asked, named as the map that
/// server plays next.
///
/// The editor connects as any client does, and stays in its menu phase: the server names its map
/// and sends this connection's nonce, the map is offered with the token's proof over it, its
/// chunks go a tick at a time, and the server says what became of them
/// (<see cref="MapTransferService"/>, <see cref="MapUploader"/>). Then the link is dropped, as the
/// menu drops one it will not play on. The token is typed for the one publish and kept nowhere.
///
/// <see cref="Publish"/> is the dialog's command and an engine check's (§11, LE8); what the status
/// line says is <see cref="MapEditorControls"/>'.
/// </summary>
public partial class MapEditorRoot
{
	/// <summary>How long a connected editor waits for the server to name its map, and with it the nonce.</summary>
	private const float PublishMapWaitSeconds = 5f;

	/// <summary>Where a publish stands.</summary>
	public PublishState Publishing { get; private set; } = PublishState.Idle;

	/// <summary>What the last publish came to, or is doing: the status line's.</summary>
	public string PublishMessage { get; private set; } = string.Empty;

	/// <summary>What the server answered the last publish's offer; null when it did not.</summary>
	public MapOfferAnswer? PublishAnswer { get; private set; }

	/// <summary>What became of the last publish's upload; null before the offer was made.</summary>
	public MapUploadState? PublishUpload { get; private set; }

	/// <summary>What the server answered the last publish's next-map request, as it came off the wire; null when it was not asked or did not answer.</summary>
	public int? PublishNextAnswer { get; private set; }

	/// <summary>The hash of the file the last publish sent.</summary>
	public string PublishedHash { get; private set; }

	private byte[] _publishFile;
	private string _publishToken;
	private string _publishName;
	private string _publishAddress;
	private bool _publishNext;
	private float _publishWait;

	private bool PublishUnderWay =>
		Publishing is PublishState.Connecting or PublishState.Sending or PublishState.AskingNext;

	/// <summary>
	/// Sends the saved map to the dedicated server at <paramref name="address"/>, proving
	/// <paramref name="token"/>, and with <paramref name="makeNext"/> asks it to play the map when it
	/// next starts. Saves first what is not saved; a map the validator finds an error in is not
	/// sent, since the server would refuse it. False, with why, when nothing was begun; true when
	/// the link is being opened — what comes of it is <see cref="Publishing"/> and
	/// <see cref="PublishMessage"/>.
	/// </summary>
	public bool Publish(string address, string token, bool makeNext, out string error)
	{
		error = "no map is open";
		if (Editing == null)
		{
			return false;
		}

		if (PublishUnderWay)
		{
			error = "a map is being published already";
			return false;
		}

		if (!MapEditorControls.TryPublishTarget(address, token, out string host, out int port, out error))
		{
			Say($"not published: {error}");
			return false;
		}

		if ((!EverSaved || Editing.Dirty || Editing.Stroking) && !Save(out error))
		{
			return false;
		}

		MapReport report = Editing.Report;
		if (!report.IsPlayable)
		{
			error = "it does not play yet";
			foreach (MapIssue issue in report.Issues)
			{
				if (issue.Severity == MapIssueSeverity.Error)
				{
					error = $"it does not play yet: {issue.Message}" + (report.Errors > 1 ? $", and {report.Errors - 1} more" : string.Empty);
					break;
				}
			}

			Say($"not published: {error}");
			ShowIssues();
			return false;
		}

		// The file as it is on the disk, which is what was saved: a file is not re-encoded on its way (LE-D15).
		if (!MapLibrary.TryReadBytes(SavedPath, out byte[] file, out error))
		{
			Say($"not published: {error}");
			return false;
		}

		string hash = MapFile.HashHex(file);
		if (hash != SavedHash)
		{
			error = $"{FileName}.map changed on the disk since it was saved here: save it again";
			Say($"not published: {error}");
			return false;
		}

		NetworkManager net = NetworkManager.Instance;
		if (net == null || !net.JoinServer(host, port))
		{
			error = net is { HasStarted: true }
				? "this process is in a session already"
				: $"could not open a socket to {host}:{port}";
			Say($"not published: {error}");
			return false;
		}

		_publishFile = file;
		_publishToken = token;
		_publishName = Editing.Document.Name;
		_publishAddress = $"{host}:{port}";
		_publishNext = makeNext;
		_publishWait = 0f;
		PublishedHash = hash;
		PublishAnswer = null;
		PublishUpload = null;
		PublishNextAnswer = null;
		Publishing = PublishState.Connecting;
		PublishMessage = $"publishing '{_publishName}' to {_publishAddress}: connecting…";
		Say(PublishMessage);
		GD.Print($"[editor] publishing {SavedPath} (sha-256 {hash}, {file.Length:N0} B) to {_publishAddress}"
			+ (makeNext ? ", to be its next map" : string.Empty));
		error = null;
		return true;
	}

	/// <summary>A publish under way, a frame on: the link, the offer, the chunks, the next map.</summary>
	private void PublishStep(float delta)
	{
		if (!PublishUnderWay)
		{
			return;
		}

		NetworkManager net = NetworkManager.Instance;
		MapTransferService transfer = net?.MapTransfer;
		if (net == null || transfer == null || !net.IsClient)
		{
			EndPublish(false, "the link is gone");
			return;
		}

		if (transfer.Upload is { } sending)
		{
			PublishAnswer = sending.Answer;
			PublishUpload = sending.State;
		}

		if (net.ConnectionFailed)
		{
			if (Publishing != PublishState.Connecting && transfer.Upload is { ServerHasIt: true } had)
			{
				// The server has the map, whatever became of the link after.
				EndPublish(true, MapEditorControls.Published(_publishName, _publishAddress, had.State, null)
					+ (_publishNext ? "; the link dropped before it said whether it plays it next" : string.Empty));
				return;
			}

			EndPublish(false, Publishing == PublishState.Connecting
				? $"no answer from {_publishAddress}: check the address, and that the server is running"
				: transfer.Upload is { Done: true } refused ? refused.Error
				: "the server dropped the link");
			return;
		}

		switch (Publishing)
		{
			case PublishState.Connecting:
				if (!net.Connected)
				{
					return;
				}

				// The nonce a proof is made over comes with the server's map, first on the connection.
				if (net.ServerMapId == null)
				{
					_publishWait += delta;
					if (_publishWait >= PublishMapWaitSeconds)
					{
						EndPublish(false, "connected, but the server never named its map: it is running an older build");
					}

					return;
				}

				if (!transfer.BeginUpload(_publishFile, _publishName, _publishToken, out string unoffered))
				{
					EndPublish(false, unoffered);
					return;
				}

				Publishing = PublishState.Sending;
				break;

			case PublishState.Sending:
				MapUploader upload = transfer.Upload;
				if (upload == null)
				{
					EndPublish(false, "the upload was let go of");
					return;
				}

				if (!upload.Done)
				{
					ShowPublishing(MapEditorControls.PublishProgress(_publishName, upload.Sent, upload.Chunks));
					return;
				}

				if (!upload.ServerHasIt)
				{
					EndPublish(false, upload.Error);
					return;
				}

				if (!_publishNext)
				{
					EndPublish(true, MapEditorControls.Published(_publishName, _publishAddress, upload.State, null));
					return;
				}

				if (!transfer.RequestNextMap(upload.Hash, _publishToken, out string unasked))
				{
					EndPublish(true, MapEditorControls.Published(_publishName, _publishAddress, upload.State, null)
						+ $"; it was not asked to play it next: {unasked}");
					return;
				}

				_publishWait = 0f;
				Publishing = PublishState.AskingNext;
				break;

			case PublishState.AskingNext:
				if (transfer.NextMapAnswer is not { } wire)
				{
					_publishWait += delta;
					if (_publishWait >= (float)MapTransfer.StallSeconds)
					{
						EndPublish(true, MapEditorControls.Published(_publishName, _publishAddress, PublishUpload ?? MapUploadState.Stored, null)
							+ $"; it did not say whether it plays it next in {MapTransfer.StallSeconds:0} s");
					}

					return;
				}

				// Published either way: the server has the map. Whether it plays it next is what the line says.
				PublishNextAnswer = wire;
				EndPublish(true, MapEditorControls.Published(_publishName, _publishAddress, PublishUpload ?? MapUploadState.Stored, wire));
				break;
		}
	}

	/// <summary>
	/// The publish is over: the link is dropped, as the menu drops one it will not play on, and the
	/// token let go of. It is <see cref="PublishState.Done"/> when the server has the map, whatever it
	/// answered about playing it next, which the line says and <see cref="PublishNextAnswer"/> holds.
	/// </summary>
	private void EndPublish(bool ok, string message)
	{
		Publishing = ok ? PublishState.Done : PublishState.Failed;
		PublishMessage = ok ? message : $"not published to {_publishAddress}: {message}";
		_publishFile = null;
		_publishToken = null;
		DropPublishLink();
		Say(PublishMessage);
	}

	private static void DropPublishLink()
	{
		PlayerManager.Instance?.ForgetClientRoster();
		NetworkManager.Instance?.AbandonConnection();
	}

	/// <summary>The editor is left with a publish under way: the link is not left for the menu to find.</summary>
	private void AbandonPublish()
	{
		if (PublishUnderWay)
		{
			Publishing = PublishState.Failed;
			PublishMessage = "not published: the editor was left";
			_publishFile = null;
			_publishToken = null;
			DropPublishLink();
			GD.PrintErr($"[editor] {PublishMessage}");
		}
	}

	private void ShowPublishing(string progress)
	{
		PublishMessage = progress;
		Message = progress;
		RefreshStatus();
	}
}
