using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PitchWise.Api.Dtos;
using PitchWise.Data;
using PitchWise.Data.Config;
using PitchWise.Data.Models;

namespace PitchWise.Api.Controllers;

// Onboarding a customer. Guarded by an operator token rather than an API key, and absent unless
// that token is configured.
[AllowAnonymous]
[ApiController]
[Route("api/admin")]
public class AdminController(AppDbContext db, AppSettings settings) : ControllerBase
{
    [HttpPost("tenants")]
    public async Task<ActionResult<CreatedTenantOut>> CreateTenant(
        [FromBody] CreateTenantIn body, [FromHeader(Name = "X-Admin-Token")] string? token)
    {
        if (string.IsNullOrEmpty(settings.AdminToken)) return NotFound();
        if (token is null || !Secrets.FixedTimeEquals(token, settings.AdminToken)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(body.Name)) return BadRequest(new { detail = "name is required" });

        var tenant = new Tenant { Name = body.Name.Trim() };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var apiKey = Secrets.Generate("pw_");
        db.ApiKeys.Add(new ApiKey { TenantId = tenant.Id, KeyHash = Secrets.Hash(apiKey) });
        await db.SaveChangesAsync();

        return new CreatedTenantOut(tenant.Id, tenant.Name, apiKey);
    }
}
