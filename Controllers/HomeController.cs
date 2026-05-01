using Microsoft.AspNetCore.Mvc;
using UENTDispatcher.Services;

namespace UENTDispatcher.Controllers;

public class HomeController : Controller
{
    private readonly DispatcherService _svc;

    public HomeController(DispatcherService svc) => _svc = svc;

    public async Task<IActionResult> Index()
    {
        var statuses = await _svc.ListEmployeeStatusesAsync();
        ViewBag.Statuses = statuses;
        return View();
    }
}
