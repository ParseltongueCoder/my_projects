using System.Collections.Concurrent;
using UofSim.Core.Producers;

namespace UofSim.Host.Feed;

public enum ProducerMode
{
    /// <summary>alive subscribed=1 every interval.</summary>
    Up,
    /// <summary>No alive at all - the consumer should detect the producer as down after its inactivity timeout.</summary>
    Silent,
    /// <summary>alive subscribed=0 - the consumer must start a recovery.</summary>
    Unsubscribed,
}

public sealed record ProducerState(int ProducerId, ProducerMode Mode, DateTimeOffset? Until, ProducerMode Then);

/// <summary>Runtime state of the simulated producers, changed via the control API and by recoveries.</summary>
public sealed class ProducerStateStore(TimeProvider clock)
{
    private readonly ConcurrentDictionary<int, ProducerState> _states = new(
        Producers.All.ToDictionary(p => p.Id, p => new ProducerState(p.Id, ProducerMode.Up, null, ProducerMode.Up)));

    public IReadOnlyCollection<ProducerState> All => Producers.All.Select(p => Get(p.Id)).ToList();

    /// <summary>Current mode; a timed mode falls back to its <c>Then</c> mode once it expires.</summary>
    public ProducerState Get(int producerId)
    {
        var state = _states[producerId];
        if (state.Until is { } until && clock.GetUtcNow() >= until)
        {
            state = new ProducerState(producerId, state.Then, null, ProducerMode.Up);
            _states[producerId] = state;
        }
        return state;
    }

    public bool Exists(int producerId) => _states.ContainsKey(producerId);

    public void Set(int producerId, ProducerMode mode, TimeSpan? duration = null, ProducerMode then = ProducerMode.Up) =>
        _states[producerId] = new ProducerState(
            producerId, mode, duration is { } d ? clock.GetUtcNow() + d : null, then);
}
