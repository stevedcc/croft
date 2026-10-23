namespace Croft.Web.Services;

/// <summary>
/// Thrown by <see cref="BlogApiClient"/> when an API call returns 401 while the user is
/// authenticated: the session has expired inside a live circuit (where no new HTTP request
/// triggers the cookie OnValidatePrincipal check), and the user has been sent to /login. It is an
/// <see cref="HttpRequestException"/> so that every page that handles a failed API call also
/// handles this one, instead of the exception ending the circuit.
/// </summary>
public class SessionExpiredException : HttpRequestException
{
    public SessionExpiredException()
        : base("The session has expired; sign in again.")
    {
    }
}
