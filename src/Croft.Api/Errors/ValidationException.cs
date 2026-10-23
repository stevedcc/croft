namespace Croft.Api.Errors;

public class ValidationException(string message) : Exception(message)
{
}
