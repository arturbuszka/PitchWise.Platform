using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PitchWise.Data;

namespace PitchWise.Api.Auth;

/// <summary>Authenticates a request by its X-Api-Key header and identifies the tenant.</summary>
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string Header = "X-Api-Key";
    public const string TenantClaim = "tenant_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var provided) || string.IsNullOrEmpty(provided))
            return AuthenticateResult.NoResult();

        var hash = Secrets.Hash(provided.ToString());
        var key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.KeyHash == hash, Context.RequestAborted);
        if (key is null) return AuthenticateResult.Fail("Invalid API key");

        var identity = new ClaimsIdentity([new Claim(TenantClaim, key.TenantId.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

public static class TenantExtensions
{
    /// <summary>The caller's tenant. Only valid on endpoints that require authentication.</summary>
    public static int TenantId(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(ApiKeyAuthenticationHandler.TenantClaim)!);
}
