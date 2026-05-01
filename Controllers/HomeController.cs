using Microsoft.AspNetCore.Mvc;
using UENTDispatcher.Services;

namespace UENTDispatcher.Controllers;

public class HomeController : Controller
{
    private readonly DispatcherService _svc;

    public HomeController(DispatcherService svc) => _svc = svc;

    public async Task<IActionResult> Index()
    {
        // Status fuers initiale Rendering — wird in der Seite per JS uebernommen.
        // Die View-seitige Serialisierung haengt nur an Vorname/Gesperrt/RestTage,
        // Nachname wird nirgends mehr angezeigt.
        var statuses = await _svc.ListEmployeeStatusesAsync();
        ViewBag.Statuses = statuses;
        return View();
    }
}
