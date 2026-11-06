using System.Security.Cryptography;
using System.Text;

namespace PitchWise.Data.Outputs;

/// <summary>A customer's key could not be used, for a reason someone can act on.</summary>
public sealed class OutputKeyException(string message) : Exception(message);

/// <summary>
/// Encrypts the keys customers give us for YouTube and Twitch. Unlike our own keys these have to be sent on,
/// so a hash won't do. The output's id is authenticated along with the key, so a row copied over another
/// one in the database doesn't decrypt.
/// </summary>
public sealed class OutputKeyCipher
{
    public const string NotConfigured =
        "OUTPUT_ENCRYPTION_KEY is not set, or isn't 32 bytes of base64, so destination keys can't be stored or read.";

    private const string Version = "v1.";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[]? _key;

    public OutputKeyCipher(string? base64Key)
    {
        try
        {
            var key = Convert.FromBase64String(base64Key ?? "");
            if (key.Length == 32) _key = key;
        }
        catch (FormatException)
        {
            // Reported as not configured, without repeating any of the value.
        }
    }

    public bool IsConfigured => _key is not null;

    public string Encrypt(Guid outputId, string plaintext)
    {
        if (_key is null) throw new OutputKeyException(NotConfigured);

        var plain = Encoding.UTF8.GetBytes(plaintext);
        var sealed_ = new byte[NonceSize + TagSize + plain.Length];
        var nonce = sealed_.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, sealed_.AsSpan(NonceSize + TagSize), sealed_.AsSpan(NonceSize, TagSize), outputId.ToByteArray());
        return Version + Convert.ToBase64String(sealed_);
    }

    public string Decrypt(Guid outputId, string encrypted)
    {
        if (_key is null) throw new OutputKeyException(NotConfigured);

        const string unreadable = "The stored destination key can't be decrypted, so OUTPUT_ENCRYPTION_KEY has probably " +
            "changed. Enter the destination's key again.";
        if (!encrypted.StartsWith(Version)) throw new OutputKeyException(unreadable);
        try
        {
            var sealed_ = Convert.FromBase64String(encrypted[Version.Length..]);
            var plain = new byte[sealed_.Length - NonceSize - TagSize];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(sealed_.AsSpan(0, NonceSize), sealed_.AsSpan(NonceSize + TagSize), sealed_.AsSpan(NonceSize, TagSize), plain, outputId.ToByteArray());
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException or OverflowException)
        {
            throw new OutputKeyException(unreadable);
        }
    }
}
