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

    public string LlmProvider { get; set; } = "openai";
    public string LlmBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string LlmApiKey { get; set; } = "";
    public string LlmModel { get; set; } = "gpt-4o-mini";

    public string WebOrigin { get; set; } = "http://localhost:3000";
    public string WebOriginAlt { get; set; } = "http://localhost:3001";

    // Name of the Redis list the vision worker pops from.
    public string VisionQueue { get; set; } = "vision_jobs";

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

    // URL of the live WebSocket + HLS server (port 8001).
    public string LiveWorkerUrl { get; set; } = "http://localhost:8001";

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
