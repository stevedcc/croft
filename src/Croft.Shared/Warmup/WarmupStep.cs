namespace Croft.Shared.Warmup;

/// <summary>What the earlier steps of one warm-up returned, by step name (steps with
/// <see cref="WarmupStep.KeepBody"/> only). A later step reads the content it needs from here.</summary>
public sealed class WarmupResults
{
    private readonly Dictionary<string, string> _bodies = [];

    public string? Body(string stepName) => _bodies.GetValueOrDefault(stepName);

    internal void Keep(string stepName, string body) => _bodies[stepName] = body;
}

/// <summary>
/// One warm-up request: a name (what the Warning calls it when it was not reached) and the path
/// it resolves when its turn comes. A step whose source is missing answers null: it is skipped,
/// not failed.
/// </summary>
/// <param name="Name">Names the step in the logs.</param>
/// <param name="ResolvePath">The path and query to GET from this process, or null to skip.</param>
/// <param name="KeepBody">Keeps the response body in <see cref="WarmupResults"/> for later steps.</param>
public sealed record WarmupStep(string Name, Func<WarmupResults, string?> ResolvePath, bool KeepBody = false)
{
    /// <summary>A step with a fixed path, named by it.</summary>
    public static WarmupStep Get(string path, bool keepBody = false) => new(path, _ => path, keepBody);
}

/// <summary>An app's warm-up requests, in the order they are sent, one at a time.</summary>
public interface IWarmupPlan
{
    IReadOnlyList<WarmupStep> Steps { get; }
}
