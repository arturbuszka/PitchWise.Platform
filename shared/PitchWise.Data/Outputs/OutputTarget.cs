namespace PitchWise.Data.Outputs;

/// <summary>Checks what a customer enters for a destination, which ends up on a command line.</summary>
public static class OutputTarget
{
    /// <summary>What's wrong with a server address, or null if it can be used.</summary>
    public static string? UrlProblem(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return "url must be a full address, such as rtmps://example.com/app";
        if (uri.Scheme is not ("rtmp" or "rtmps"))
            return "url must start with rtmp:// or rtmps://";
        if (url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            return "url can't contain spaces";
        // Those would come back from the API and appear in logs, where a key must not.
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return "url can't contain a login or parameters: put the key in stream_key";
        return null;
    }

    /// <summary>What's wrong with a stream key, or null if it can be used.</summary>
    public static string? KeyProblem(string key)
    {
        if (string.IsNullOrEmpty(key)) return "stream_key is required";
        if (key.Length > 512) return "stream_key is too long";
        // Printable ASCII. A slash would change which path on the server it publishes to.
        if (key.Any(c => c is <= ' ' or > '~' or '/')) return "stream_key can't contain spaces or slashes";
        return null;
    }

    public static string Publish(string url, string key) => $"{url.TrimEnd('/')}/{key}";
}
