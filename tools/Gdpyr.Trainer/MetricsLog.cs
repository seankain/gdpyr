using System;
using System.Globalization;
using System.IO;

namespace Gdpyr.Trainer;

/// <summary>
/// One CSV row per report window, so a run can be plotted after it finishes
/// without scraping the console.
///
/// A file rather than a dashboard on purpose: RLMatrix's own dashboard wants a
/// process listening on <c>localhost:7126</c> and prints a line about it when
/// nothing is, and a training run that has to be watched live is a training run
/// nobody can leave alone overnight. The columns are the ones an RL run is
/// actually read by — the reward this repository made up, and the win rate the
/// game decided (docs/RL_ARCHITECTURE.md §6).
/// </summary>
public sealed class MetricsLog : IDisposable
{
	private readonly StreamWriter _writer;

	public MetricsLog(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return;
		}

		string directory = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		bool fresh = !File.Exists(path) || new FileInfo(path).Length == 0;
		_writer = new StreamWriter(path, append: true) { AutoFlush = true };
		if (fresh)
		{
			_writer.WriteLine("wall_clock,step,episodes,last_episode_reward,reward_in_flight,wins,losses,fallbacks");
		}
	}

	public void Write(int step, int episodes, float lastEpisodeReward, float rewardInFlight, int wins,
		int losses, int fallbacks)
	{
		if (_writer == null)
		{
			return;
		}

		_writer.WriteLine(string.Join(',',
			DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
			step.ToString(CultureInfo.InvariantCulture),
			episodes.ToString(CultureInfo.InvariantCulture),
			lastEpisodeReward.ToString("0.0000", CultureInfo.InvariantCulture),
			rewardInFlight.ToString("0.0000", CultureInfo.InvariantCulture),
			wins.ToString(CultureInfo.InvariantCulture),
			losses.ToString(CultureInfo.InvariantCulture),
			fallbacks.ToString(CultureInfo.InvariantCulture)));
	}

	public void Dispose() => _writer?.Dispose();
}
