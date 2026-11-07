using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PitchWise.Data.Config;

namespace PitchWise.Api.Services;

/// <summary>
/// Signs the address an operator's browser uses to watch one signal over WebRTC. MediaMTX hands the token to our
/// auth callback, which checks it here. Only needed to connect, so it can be short-lived, and it opens nothing else.
/// </summary>
public class PreviewSigner(AppSettings settings, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public (string Token, DateTime ExpiresAt) Sign(string path)
    {
        var expires = time.GetUtcNow() + Lifetime;
        var epoch = expires.ToUnixTimeSeconds();
        return ($"{epoch}.{Signature(path, epoch)}", expires.UtcDateTime);
    }

    public bool IsValid(string path, string token)
    {
        // Without the secret, anyone could work out a valid signature.
        if (settings.MediaMtxReadSecret.Length == 0) return false;

        var parts = token.Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
            return false;
        if (epoch < time.GetUtcNow().ToUnixTimeSeconds()) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(parts[1]), Encoding.UTF8.GetBytes(Signature(path, epoch)));
    }

    private string Signature(string path, long epoch)
    {
        var mac = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(settings.MediaMtxReadSecret), Encoding.UTF8.GetBytes($"preview|{path}|{epoch}"));
        return Convert.ToHexStringLower(mac);
    }
}
