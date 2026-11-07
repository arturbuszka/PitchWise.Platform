using System.Net;
using System.Net.Sockets;

namespace PitchWise.Broadcast;

public interface ITargetGuard
{
    /// <summary>Why a destination may not be used, or null. Throws if its name can't be looked up.</summary>
    Task<string?> ProblemWithAsync(string url, CancellationToken ct);
}

/// <summary>
/// Customers choose where outputs connect, from inside our network. Without this, one could point an output
/// at the database or MediaMTX's control API and read the result from the error it gets back. It can't stop a
/// name that changes its answer between this check and ffmpeg's own lookup.
/// </summary>
public sealed class TargetGuard(BroadcastSettings settings) : ITargetGuard
{
    private static readonly TimeSpan LookupLimit = TimeSpan.FromSeconds(5);

    public async Task<string?> ProblemWithAsync(string url, CancellationToken ct)
    {
        if (settings.AllowPrivateTargets) return null;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(LookupLimit);
        var addresses = await Dns.GetHostAddressesAsync(new Uri(url).Host, limit.Token);
        return addresses.Any(IsPrivate) ? "That address is on a private network, which outputs can't send to." : null;
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] is 0 or 10 or 127 or >= 224
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal
            || address.Equals(IPAddress.IPv6Any);
    }
}
