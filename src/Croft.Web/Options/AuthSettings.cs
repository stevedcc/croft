using System.Globalization;

namespace Croft.Web.Options;

/// <summary>
/// The <c>Auth:*</c> settings. <c>Auth:SessionLifetime</c> is how long a login session lives when
/// the IdP's token response does not say how long its refresh token lives (a .NET time span such
/// as <c>30.00:00:00</c>). An invalid value is a deployment error and fails startup naming the
/// setting.
/// </summary>
public sealed record AuthSettings(TimeSpan SessionLifetime)
{
    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromDays(30);

    private static readonly TimeSpan MaxSessionLifetime = TimeSpan.FromDays(365);

    private const string SessionLifetimeSetting = "Auth:SessionLifetime";

    public static AuthSettings Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[SessionLifetimeSetting];
        if (raw is null)
        {
            return new(DefaultSessionLifetime);
        }

        if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var lifetime)
            || lifetime <= TimeSpan.Zero || lifetime > MaxSessionLifetime)
        {
            throw new InvalidOperationException(
                $"{SessionLifetimeSetting} must be a time span greater than zero and at most 365 days, got '{raw}'.");
        }

        return new(lifetime);
    }
}
