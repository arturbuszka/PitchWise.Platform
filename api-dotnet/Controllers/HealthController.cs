using Microsoft.AspNetCore.Mvc;
using PitchWise.Data.Config;

namespace PitchWise.Api.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    private readonly AppSettings _settings;

    public HealthController(AppSettings settings) => _settings = settings;

    [HttpGet]
    public object Health() => new
    {
        status = "ok",
        llm_provider = _settings.LlmProvider,
    };
}
