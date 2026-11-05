using System.Security.Cryptography;
using System.Text;

namespace PitchWise.Data;

/// <summary>API keys and stream keys. They are long random values rather than passwords, so a
/// plain SHA-256 is enough to make a leaked database useless.</summary>
public static class Secrets
{
    public static string Generate(string prefix) =>
        prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>A short, unguessable id that is safe to put in a public URL.</summary>
    public static string GeneratePublicId() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('+', '-').Replace('/', '_');

    public static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Hash(a)), Encoding.UTF8.GetBytes(Hash(b)));
}
