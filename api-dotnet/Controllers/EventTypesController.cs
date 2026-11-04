using Microsoft.AspNetCore.Mvc;
using PitchWise.Data.Config;
using PitchWise.Api.Dtos;

namespace PitchWise.Api.Controllers;

// The event types the UI offers. The sport parameter is accepted but not used yet.
[ApiController]
[Route("api/event-types")]
public class EventTypesController : ControllerBase
{
    [HttpGet]
    public IReadOnlyList<EventTypeConfigOut> List([FromQuery] string? sport) => EventTypeConfig.All;
}
