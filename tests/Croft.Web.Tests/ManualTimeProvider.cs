namespace Croft.Web.Tests;

/// <summary>A <see cref="TimeProvider"/> whose timers fire only when the test advances the clock,
/// so a background loop's waits are driven by the test and nothing sleeps. A one-shot timer is
/// enough for <c>Task.Delay(delay, timeProvider)</c>.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];
    private DateTimeOffset _now = utcNow;
    private int _created;

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, _now + dueTime);
        lock (_gate)
        {
            _timers.Add(timer);
            _created++;
            foreach (var waiter in _waiters.Where(w => _created >= w.Count))
            {
                waiter.Signal.TrySetResult();
            }
        }

        return timer;
    }

    /// <summary>Completes once at least <paramref name="count"/> timers have been created in total.</summary>
    public Task WaitForTimersAsync(int count)
    {
        lock (_gate)
        {
            if (_created >= count)
            {
                return Task.CompletedTask;
            }

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, signal));
            return signal.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Moves the clock forward, fires the timers that are due, then waits until the loop
    /// under test has created its next timer (<paramref name="expectTimers"/> in total), meaning
    /// the tick that the advance caused has finished.</summary>
    public async Task AdvanceAsync(TimeSpan by, int expectTimers)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(t => !t.Fired && t.Due <= _now)];
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }

        await WaitForTimersAsync(expectTimers);
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
    {
        public DateTimeOffset Due { get; } = due;

        public bool Fired { get; private set; }

        public void Fire()
        {
            Fired = true;
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
