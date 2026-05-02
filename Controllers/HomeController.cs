using Microsoft.AspNetCore.Mvc;
using UENTDispatcher.Services;

namespace UENTDispatcher.Controllers;

public class HomeController : Controller
{
    private readonly DispatcherService _svc;
    private readonly PermissionService _perm;

    public HomeController(DispatcherService svc, PermissionService perm)
    {
        _svc = svc;
        _perm = perm;
    }

    public async Task<IActionResult> Index()
    {
        // Status fuers initiale Rendering — wird in der Seite per JS uebernommen.
        // Die View-seitige Serialisierung haengt nur an Vorname/Gesperrt/RestTage,
        // Nachname wird nirgends mehr angezeigt.
        var statuses = await _svc.ListEmployeeStatusesAsync();
        ViewBag.Statuses = statuses;
        ViewBag.SperreTage = await _svc.GetSperreTageAsync();
        ViewBag.Permissions = await _perm.GetForAsync(User);
        return View();
    }
}
