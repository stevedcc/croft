namespace Croft.Api.Errors;

public class ForbiddenException(string message) : Exception(message)
{
}
