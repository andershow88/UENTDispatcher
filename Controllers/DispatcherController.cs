using Microsoft.AspNetCore.Authorization;
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

    /// <summary>
    /// Loescht den gesamten Verlauf. Achtung: hebt damit auch alle aktuellen
    /// Sperren auf. Reserviert fuer Admins. Manueller Role-Check statt
    /// [Authorize(Roles=)], damit Cookie-Auth keinen 302-Redirect produziert.
    /// </summary>
    [HttpPost("ClearLog")]
    public async Task<IActionResult> ClearLog()
    {
        if (!User.IsInRole("Admin"))
            return Json(new { ok = false, error = "Nur Admins duerfen den Verlauf loeschen." });
        var count = await _svc.ClearLogAsync();
        return Json(new { ok = true, geloescht = count });
    }

    public record DeleteEntryRequest(int Id);

    /// <summary>
    /// Loescht einen einzelnen Verlaufseintrag. Hebt damit ggf. die durch
    /// diesen Eintrag bestimmte Sperre auf, falls er der juengste war.
    /// </summary>
    [HttpPost("DeleteEntry")]
    public async Task<IActionResult> DeleteEntry([FromBody] DeleteEntryRequest req)
    {
        if (req == null || req.Id <= 0)
            return Json(new { ok = false, error = "Ungueltige Anfrage." });
        var result = await _svc.DeleteEntryAsync(req.Id);
        return Json(new { ok = result.Erfolgreich, error = result.Fehler });
    }
}
