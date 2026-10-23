namespace Croft.Shared.Warmup;

/// <summary>
/// The readiness condition the warm-up holds: closed from start until the warm-up finishes, fails
/// or runs out of time, then open for good. Open from the start when the warm-up is disabled.
/// Readiness checks ask <see cref="IsOpen"/> first; the gate never closes again, so a pod that was
/// ready stays ready.
/// </summary>
public sealed class WarmupGate
{
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WarmupGate(bool enabled)
    {
        if (!enabled)
        {
            Open();
        }
    }

    public bool IsOpen => _opened.Task.IsCompleted;

    /// <summary>Completes when the gate opens (tests await it instead of sleeping).</summary>
    public Task WhenOpen => _opened.Task;

    public void Open() => _opened.TrySetResult();
}
