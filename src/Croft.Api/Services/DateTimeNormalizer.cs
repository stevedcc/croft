namespace Croft.Api.Services;

public class DateTimeNormalizer(TimeProvider time)
{
    public DateTime NormalizeToUtc(DateTime? value)
    {
        if (!value.HasValue)
            return time.GetUtcNow().UtcDateTime;

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }
}
