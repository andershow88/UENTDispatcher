using Microsoft.AspNetCore.Mvc;
using UENTDispatcher.Services;

namespace UENTDispatcher.Controllers;

/// <summary>
/// API-Endpunkte fuer das Glücksrad: Spin, Confirm, Log.
/// Wird aus der Wheel-Page (Home/Index) per fetch() angesprochen.
/// </summary>
[Route("[controller]")]
public class DispatcherController : Controller
{
    private readonly DispatcherService _svc;
    public DispatcherController(DispatcherService svc) => _svc = svc;

    public record SpinRequest(bool BlacklistIgnoriert);

    [HttpPost("Spin")]
    public async Task<IActionResult> Spin([FromBody] SpinRequest? req)
    {
        var includeBlocked = req?.BlacklistIgnoriert ?? false;
        var result = await _svc.SpinAsync(includeBlocked);
        return Json(new
        {
            ok = result.Erfolgreich,
            error = result.Fehler,
            winner = result.Auswahl == null ? null : new
            {
                id = result.Auswahl.Id,
                vorname = result.Auswahl.Vorname,
                nachname = result.Auswahl.Nachname,
                anzeigename = $"{result.Auswahl.Vorname} {result.Auswahl.Nachname}",
                gesperrt = result.Auswahl.Gesperrt,
                restTage = result.Auswahl.RestTage
            },
            kandidaten = result.AlleKandidaten.Select(s => new
            {
                id = s.Id,
                anzeigename = $"{s.Vorname} {s.Nachname}",
                gesperrt = s.Gesperrt,
                restTage = s.RestTage
            })
        });
    }

    public record ConfirmRequest(int EmployeeId, bool BlacklistIgnoriert);

    [HttpPost("Confirm")]
    public async Task<IActionResult> Confirm([FromBody] ConfirmRequest req)
    {
        if (req == null || req.EmployeeId <= 0)
            return Json(new { ok = false, error = "Ungueltige Anfrage." });

        var result = await _svc.ConfirmAsync(req.EmployeeId, req.BlacklistIgnoriert);
        return Json(new
        {
            ok = result.Erfolgreich,
            error = result.Fehler,
            info = result.Info == null ? null : new
            {
                employeeId = result.Info.EmployeeId,
                anzeigename = result.Info.Anzeigename,
                bestaetigtUtc = result.Info.BestaetigtUtc,
                sperrBisUtc = result.Info.SperrBisUtc,
                restTageUebernommen = result.Info.RestTageUebernommen,
                blacklistIgnoriert = result.Info.BlacklistIgnoriert
            }
        });
    }

    [HttpGet("Status")]
    public async Task<IActionResult> Status()
    {
        var statuses = await _svc.ListEmployeeStatusesAsync();
        return Json(statuses.Select(s => new
        {
            id = s.Id,
            anzeigename = $"{s.Vorname} {s.Nachname}",
            gesperrt = s.Gesperrt,
            restTage = s.RestTage,
            sperrBisUtc = s.SperrBisUtc
        }));
    }

    [HttpGet("Log")]
    public async Task<IActionResult> Log()
    {
        var entries = await _svc.ListLogAsync();
        return View(entries);
    }
}
