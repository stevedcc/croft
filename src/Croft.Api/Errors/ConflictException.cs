namespace Croft.Api.Errors;

public class ConflictException(string message) : Exception(message)
{
}
