namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// A fake clock whose timers follow it: <see cref="Advance"/> fires every timer whose due time
/// the jump passes, in due order, at the fake instant it falls due — so production code that
/// sleeps with <c>Task.Delay(delay, timeProvider, ct)</c> (the scheduler loop) runs on simulated
/// time. The base <see cref="TimeProvider"/> implementation backs <c>CreateTimer</c> with a real
/// system timer, which is why a provider that only overrides <see cref="GetUtcNow"/> cannot drive
/// sleeps; tests that need neither can keep using <see cref="FakeTimeProvider"/>.
/// </summary>
internal sealed class FakeTimerTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = utcNow;
    private readonly List<TimerEntry> _timers = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan delta)
    {
        List<(TimerCallback Callback, object? State)> due = [];
        lock (_gate)
        {
            var target = _now + delta;
            while (_timers
                    .Where(t => !t.Disposed && t.Due <= target)
                    .MinBy(t => t.Due) is { } next)
            {
                // The clock passes through the due instant, exactly as real time would.
                _now = next.Due;
                if (next.Period == Timeout.InfiniteTimeSpan)
                {
                    next.Disposed = true;
                }
                else
                {
                    next.Due += next.Period;
                }

                due.Add((next.Callback, next.State));
            }

            _now = target;
            SignalChange();
        }

        // Callbacks run outside the lock: a callback that registers another timer must not
        // deadlock against the Advance that fired it.
        foreach (var (callback, state) in due)
        {
            callback(state);
        }
    }

    /// <summary>
    /// Waits until the provider holds a pending future timer — the observable shape of "the code
    /// under test is asleep on this clock" — bounded so a loop that never sleeps surfaces as a
    /// failed test, not a hung run.
    /// </summary>
    public async Task WaitUntilSleepingAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_timers.Any(t => !t.Disposed && t.Due > _now))
                {
                    return;
                }

                wait = _changed.Task;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    "No pending timer appeared on the fake clock; the code under test never went to sleep on it.");
            }

            await wait.WaitAsync(remaining);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (_gate)
        {
            var entry = new TimerEntry(this, callback, state, _now + dueTime, period);
            _timers.Add(entry);
            SignalChange();
            return entry;
        }
    }

    private void SignalChange()
    {
        _changed.TrySetResult();
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TimerEntry(
        FakeTimerTimeProvider owner,
        TimerCallback callback,
        object? state,
        DateTimeOffset due,
        TimeSpan period) : ITimer
    {
        public TimerCallback Callback => callback;
        public object? State => state;
        public DateTimeOffset Due = due;
        public readonly TimeSpan Period = period;
        public bool Disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Due = owner._now + dueTime;
                owner.SignalChange();
            }

            return true;
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
