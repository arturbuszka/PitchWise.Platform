using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using PitchWise.Data;

namespace PitchWise.Api.Auth;

/// <summary>
/// Stops a tenant reaching another tenant's analysis. Every route below an analysis names it as
/// {analysisId}, so checking it here covers videos, events and highlights without any of those
/// endpoints having to remember. Answers 404, not 403, so ids of other tenants stay unconfirmed.
/// </summary>
public class AnalysisAccessFilter(AppDbContext db) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.RouteData.Values.TryGetValue("analysisId", out var value)
            && int.TryParse(value?.ToString(), out var analysisId))
        {
            var tenantId = context.HttpContext.User.TenantId();
            if (!await db.AnalysisSessions.AnyAsync(a => a.Id == analysisId && a.TenantId == tenantId))
            {
                context.Result = new NotFoundObjectResult(new { detail = "Analysis not found" });
                return;
            }
        }
        await next();
    }
}
