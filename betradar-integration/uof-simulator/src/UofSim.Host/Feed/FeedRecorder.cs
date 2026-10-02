using System.Text;
using Microsoft.Extensions.Options;
using UofSim.Core.Recordings;

namespace UofSim.Host.Feed;

public sealed record RecorderStatus(string? File, bool Recording, int Messages);

/// <summary>
/// Records every published event message (not alive/snapshot_complete) to a JSONL file in the data
/// directory, in the same format the replayer reads - so any scenario run can be replayed later.
/// </summary>
public sealed class FeedRecorder(IOptions<SimOptions> options, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly List<RecordedMessage> _messages = [];
    private string? _file;
    private long _startedAt;

    public RecorderStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new RecorderStatus(_file, _file is not null, _messages.Count);
            }
        }
    }

    public RecorderStatus Start(string file)
    {
        var path = DataPaths.Resolve(options.Value.DataDir, file);
        lock (_gate)
        {
            _file = path;
            _messages.Clear();
            _startedAt = clock.GetTimestamp();
            return new RecorderStatus(_file, true, 0);
        }
    }

    /// <summary>Stops recording and writes the file.</summary>
    public RecorderStatus Stop()
    {
        lock (_gate)
        {
            if (_file is null)
            {
                return new RecorderStatus(null, false, 0);
            }
            Recording.Write(_file, _messages);
            var status = new RecorderStatus(_file, false, _messages.Count);
            _file = null;
            return status;
        }
    }

    public void Observe(string routingKey, byte[] body)
    {
        // System messages carry no event data and are regenerated live by the simulator.
        if (routingKey.StartsWith("-.-.-.", StringComparison.Ordinal))
        {
            return;
        }
        lock (_gate)
        {
            if (_file is not null)
            {
                var offset = (long)clock.GetElapsedTime(_startedAt).TotalMilliseconds;
                _messages.Add(new RecordedMessage(offset, routingKey, Encoding.UTF8.GetString(body)));
            }
        }
    }
}

public static class DataPaths
{
    /// <summary>Resolves a path relative to the data directory and refuses anything outside it.</summary>
    public static string Resolve(string dataDir, string relative)
    {
        var root = Path.GetFullPath(dataDir);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Path must be inside the data directory", nameof(relative));
        }
        return path;
    }
}
