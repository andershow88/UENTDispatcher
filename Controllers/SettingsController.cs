using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UENTDispatcher.Models;

namespace UENTDispatcher.Controllers;

/// <summary>
/// Globale App-Einstellungen pflegen. Aktuell nur die Sperrfrist; bei Bedarf
/// einfach erweiterbar (zusaetzliche Felder im AppSettings-Modell + UI-Block).
/// </summary>
public class SettingsController : Controller
{
    private readonly AppDbContext _db;
    private readonly ILogger<SettingsController> _log;

    public SettingsController(AppDbContext db, ILogger<SettingsController> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<IActionResult> Index()
    {
        var settings = await _db.AppSettings.FirstOrDefaultAsync()
                       ?? new AppSettings { Id = 1, SperreTage = 21 };
        return View(settings);
    }

    public record UpdateRequest(int SperreTage);

    [HttpPost]
    public async Task<IActionResult> Update([FromBody] UpdateRequest req)
    {
        if (req == null) return Json(new { ok = false, error = "Ungueltige Anfrage." });
        if (req.SperreTage < 0 || req.SperreTage > 365)
            return Json(new { ok = false, error = "Sperrfrist muss zwischen 0 und 365 Tagen liegen." });

        var settings = await _db.AppSettings.FirstOrDefaultAsync();
        if (settings == null)
        {
            settings = new AppSettings { Id = 1, SperreTage = req.SperreTage, ZuletztGeaendertUtc = DateTime.UtcNow };
            _db.AppSettings.Add(settings);
        }
        else
        {
            settings.SperreTage = req.SperreTage;
            settings.ZuletztGeaendertUtc = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        _log.LogInformation("AppSettings aktualisiert: SperreTage={Tage}", req.SperreTage);
        return Json(new { ok = true, sperreTage = settings.SperreTage });
    }
}
