using Microsoft.AspNetCore.Mvc;
using UENTDispatcher.Services;

namespace UENTDispatcher.Controllers;

public class HinweiseController : Controller
{
    private readonly DispatcherService _svc;
    public HinweiseController(DispatcherService svc) => _svc = svc;

    public async Task<IActionResult> Index()
    {
        ViewBag.SperreTage = await _svc.GetSperreTageAsync();
        return View();
    }
}
