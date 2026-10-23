namespace Croft.Api.Errors;

public class NotFoundException(string message) : Exception(message)
{
}
