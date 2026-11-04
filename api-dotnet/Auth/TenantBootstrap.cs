using Microsoft.EntityFrameworkCore;
using PitchWise.Data;
using PitchWise.Data.Models;

namespace PitchWise.Api.Auth;

/// <summary>Creates the demo tenant for local use and the web console, when a key is configured.</summary>
public static class TenantBootstrap
{
    public static async Task EnsureAsync(AppDbContext db, string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return;

        var hash = Secrets.Hash(apiKey);
        if (await db.ApiKeys.AnyAsync(k => k.KeyHash == hash)) return;

        var tenant = new Tenant { Name = "Demo" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        db.ApiKeys.Add(new ApiKey { TenantId = tenant.Id, KeyHash = hash });
        await db.SaveChangesAsync();
    }
}
