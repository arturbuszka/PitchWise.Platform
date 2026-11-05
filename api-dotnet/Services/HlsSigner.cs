using System.Security.Cryptography;
using System.Text;
using PitchWise.Data.Config;

namespace PitchWise.Api.Services;

// Mints signed, expiring HLS URLs that nginx validates on its own (ngx_http_secure_link_module),
// keeping the API off the byte path.
//
// A single token must authorize the whole HLS directory (manifest + every segment), because
// the player fetches segment_*.ts autonomously and we can't re-sign each one. So the signature
// covers the DIRECTORY PREFIX "/hls/{id}/", not the per-file $uri. nginx recreates the same
// prefix from the request path with a regex capture, so manifest and segments validate against
// the same hash. The nginx directive must therefore be:
//
//   secure_link_md5 "$secure_link_expires $dir <secret>";   # $dir = "/hls/{id}/"
//
// where md5 is base64url-encoded the nginx way: '+'→'-', '/'→'_', '=' stripped.
public class HlsSigner
{
    private readonly AppSettings _settings;

    public HlsSigner(AppSettings settings)
    {
        _settings = settings;
    }

    // Builds a signed URL for a highlight's HLS manifest. md5/expires also authorize the
    // sibling segments under the same /hls/{id}/ prefix until the token expires.
    public (string Url, DateTimeOffset ExpiresAt) SignHighlight(int highlightId)
    {
        var dir = $"/hls/{highlightId}/";
        var expires = DateTimeOffset.UtcNow.AddSeconds(_settings.HlsLinkTtlSeconds);
        var epoch = expires.ToUnixTimeSeconds();

        var toSign = $"{epoch} {dir} {_settings.HlsSigningSecret}";
        var md5 = Base64Url(MD5.HashData(Encoding.UTF8.GetBytes(toSign)));

        var url = $"{_settings.HlsBaseUrl.TrimEnd('/')}{dir}index.m3u8?md5={md5}&expires={epoch}";
        return (url, expires);
    }

    // A live stream's playlists and segments change constantly and some players drop query
    // strings, so the signature sits in the path, where relative URLs inherit it. It covers
    // "/live/{id}/", which lets the edge share one cache entry per segment between all viewers.
    public (string Url, DateTimeOffset ExpiresAt) SignLive(Guid inputId)
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(_settings.HlsLiveLinkTtlSeconds);
        var epoch = expires.ToUnixTimeSeconds();
        var toSign = $"{epoch} /live/{inputId}/ {_settings.HlsSigningSecret}";
        var md5 = Base64Url(MD5.HashData(Encoding.UTF8.GetBytes(toSign)));
        return ($"{_settings.HlsBaseUrl.TrimEnd('/')}/live/{md5}/{epoch}/{inputId}/index.m3u8", expires);
    }

    // A finished recording, as one file with Range requests. It lasts as long as a live link, since
    // a player keeps asking for parts of the file while someone watches.
    public (string Url, DateTimeOffset ExpiresAt) SignVod(string filename)
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(_settings.HlsLiveLinkTtlSeconds);
        var epoch = expires.ToUnixTimeSeconds();
        var toSign = $"{epoch} /vod/{filename} {_settings.HlsSigningSecret}";
        var md5 = Base64Url(MD5.HashData(Encoding.UTF8.GetBytes(toSign)));
        return ($"{_settings.HlsBaseUrl.TrimEnd('/')}/vod/{md5}/{epoch}/{filename}", expires);
    }

    // nginx-style base64url: standard base64, '+'→'-', '/'→'_', no '=' padding.
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
