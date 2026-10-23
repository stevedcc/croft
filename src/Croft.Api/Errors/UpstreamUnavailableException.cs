namespace Croft.Api.Errors;

/// <summary>A service the request depends on (Bluesky's handle resolution at save) cannot answer: the API answers 503, never a silent save.</summary>
public class UpstreamUnavailableException(string message, Exception? inner = null) : Exception(message, inner)
{
}
