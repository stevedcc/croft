namespace Croft.Api.Services;

/// <summary>
/// The scheduler's wakeup line: the post service signals it the moment a post is scheduled,
/// rescheduled or unscheduled, so the scheduler can re-read the earliest due time and sleep to
/// it instead of waiting out its cap. Singleton: one signal per API instance, and the loop must
/// see the same instance the writers do.
/// </summary>
public interface IScheduledPublishWakeup
{
    /// <summary>Signals the scheduler to wake and re-read the earliest due time.</summary>
    void Wake();

    /// <summary>
    /// A task that completes on the next <see cref="Wake"/>; capture it before a tick so a wake
    /// that fires during the tick is not lost.
    /// </summary>
    Task Next { get; }
}

public sealed class ScheduledPublishWakeup : IScheduledPublishWakeup
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _signalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Wake()
    {
        lock (_gate)
        {
            _signalled.TrySetResult();
            _signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public Task Next
    {
        get
        {
            lock (_gate)
            {
                return _signalled.Task;
            }
        }
    }
}
