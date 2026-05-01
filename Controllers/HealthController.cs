using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace UENTDispatcher.Controllers;

[AllowAnonymous]
[Route("[controller]")]
public class HealthController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => Ok(new { status = "ok", utc = DateTime.UtcNow });
}
