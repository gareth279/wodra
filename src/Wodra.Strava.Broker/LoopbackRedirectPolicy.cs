namespace Wodra.Strava.Broker;

public static class LoopbackRedirectPolicy
{
    public static bool IsAllowed(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/strava/callback" || uri.Port is < 1 or > 65535)
            return false;

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               System.Net.IPAddress.TryParse(uri.Host, out var address) &&
               System.Net.IPAddress.IsLoopback(address);
    }
}
