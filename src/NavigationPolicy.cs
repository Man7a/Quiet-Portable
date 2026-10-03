namespace QuietGPT;

internal static class NavigationPolicy
{
    public static bool IsHost(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    public static bool IsInternal(string address)
    {
        if (address.StartsWith("blob:https://", StringComparison.OrdinalIgnoreCase))
            return IsInternal(address[5..]);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0) return false;
        return new[] { "chatgpt.com", "openai.com", "oaiusercontent.com", "oaistatic.com" }.Any(d => IsHost(uri.IdnHost, d)) || IsAuthentication(address);
    }

    public static bool IsAuthentication(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0) return false;
        return new[] { "auth.openai.com", "auth0.openai.com", "accounts.google.com", "appleid.apple.com", "account.apple.com", "login.microsoftonline.com", "login.live.com" }
            .Any(d => IsHost(uri.IdnHost, d));
    }

    public static bool IsExternalWeb(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0;
}
