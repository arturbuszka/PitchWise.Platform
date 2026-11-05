namespace PitchWise.Data.Config;

// Settings shared by the API and the worker, loaded from the environment and
// appsettings.json.
public class AppSettings
{
    public string StorageDir { get; set; } = "./storage";

    // A standard Npgsql connection string.
    public string DatabaseConnection { get; set; } =
        "Host=localhost;Port=5432;Database=pitchwise;Username=pitchwise;Password=pitchwise";

    public string RedisUrl { get; set; } = "localhost:6379";

    public string WebOrigin { get; set; } = "http://localhost:3000";
    public string WebOriginAlt { get; set; } = "http://localhost:3001";

    // Name of the Redis list the highlight worker loop pops from.
    public string HighlightQueue { get; set; } = "highlight_jobs";

    // How long a generated share link stays valid.
    public int ShareLinkTtlHours { get; set; } = 24;

    // --- HLS delivery (CDN-style; the API mints signed URLs, nginx serves bytes) ---
    // Browser-facing base URL of the nginx edge that serves HLS segments.
    public string HlsBaseUrl { get; set; } = "http://localhost:8080";
    // Shared secret for nginx secure_link signatures. MUST match the nginx env.
    public string HlsSigningSecret { get; set; } = "devsecret";
    // How long a signed HLS URL stays valid.
    public int HlsLinkTtlSeconds { get; set; } = 3600;
    // The same for a live match. It must outlast a broadcast, since a player keeps using its URL.
    public int HlsLiveLinkTtlSeconds { get; set; } = 21600;

    // --- Tenancy ---
    // Required by POST /api/admin/tenants. Empty turns the endpoint off.
    public string AdminToken { get; set; } = "";
    // When set, startup makes sure a demo tenant has this key, for local use and the web console.
    public string BootstrapApiKey { get; set; } = "";

    // --- Live ingest (MediaMTX) ---
    // Where encoders publish, as they see it.
    public string RtmpIngestUrl { get; set; } = "rtmp://localhost:1935";
    public string SrtIngestUrl { get; set; } = "srt://localhost:8890";
    // MediaMTX control API, reachable only from inside the deployment.
    public string MediaMtxApiUrl { get; set; } = "http://localhost:9997";
    // Part of the auth callback URL, so only MediaMTX can reach that endpoint.
    public string MediaMtxAuthSecret { get; set; } = "devsecret";
    public int MediaMtxPollSeconds { get; set; } = 2;

    public string UploadsDir => Path.Combine(StorageDir, "uploads");
    public string ClipsDir => Path.Combine(StorageDir, "clips");
    public string HlsDir => Path.Combine(StorageDir, "hls");

    public void EnsureDirs()
    {
        Directory.CreateDirectory(StorageDir);
        Directory.CreateDirectory(UploadsDir);
        Directory.CreateDirectory(ClipsDir);
        Directory.CreateDirectory(HlsDir);
    }
}
