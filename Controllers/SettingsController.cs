using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UENTDispatcher.Models;

namespace UENTDispatcher.Controllers;

/// <summary>
/// Globale App-Einstellungen + Benutzerverwaltung. Admin-only — manuelle
/// Role-Checks pro Action, damit JSON statt Redirect zurueckkommt.
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
        if (!User.IsInRole("Admin")) return RedirectToAction("Index", "Home");
        var settings = await _db.AppSettings.FirstOrDefaultAsync()
                       ?? new AppSettings { Id = 1, SperreTage = 21 };
        var users = await _db.Users.OrderBy(u => u.Benutzername).ToListAsync();
        ViewBag.Users = users;
        return View(settings);
    }

    public record UpdateRequest(int SperreTage);

    [HttpPost]
    public async Task<IActionResult> Update([FromBody] UpdateRequest req)
    {
        if (!User.IsInRole("Admin")) return Json(new { ok = false, error = "Nur Admins." });
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

    // ── Benutzerverwaltung ──────────────────────────────────────────────

    public record CreateUserRequest(string Benutzername, string Anzeigename, string Passwort, string Rolle);

    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
    {
        if (!User.IsInRole("Admin")) return Json(new { ok = false, error = "Nur Admins." });
        if (req == null) return Json(new { ok = false, error = "Ungueltige Anfrage." });
        var benutzername = (req.Benutzername ?? "").Trim();
        var anzeigename = (req.Anzeigename ?? "").Trim();
        var passwort = req.Passwort ?? "";
        var rolle = (req.Rolle ?? "").Trim();

        if (string.IsNullOrEmpty(benutzername))
            return Json(new { ok = false, error = "Benutzername ist Pflicht." });
        if (benutzername.Length > 100)
            return Json(new { ok = false, error = "Benutzername ist zu lang (max. 100)." });
        if (string.IsNullOrEmpty(anzeigename))
            return Json(new { ok = false, error = "Anzeigename ist Pflicht." });
        if (passwort.Length < 4 || passwort.Length > 200)
            return Json(new { ok = false, error = "Passwort muss zwischen 4 und 200 Zeichen lang sein." });
        if (rolle != "Admin" && rolle != "User")
            return Json(new { ok = false, error = "Rolle muss 'Admin' oder 'User' sein." });

        var exists = await _db.Users.AnyAsync(u => u.Benutzername == benutzername);
        if (exists) return Json(new { ok = false, error = "Benutzername ist bereits vergeben." });

        var u = new AppUser
        {
            Benutzername = benutzername,
            Anzeigename = anzeigename,
            PasswortHash = Hash(passwort),
            Rolle = rolle,
            IstAktiv = true,
            ErstelltAm = DateTime.UtcNow
        };
        _db.Users.Add(u);
        await _db.SaveChangesAsync();
        _log.LogInformation("Neuer Benutzer angelegt: {User} (Rolle={Rolle})", benutzername, rolle);
        return Json(new { ok = true, id = u.Id });
    }

    public record DeleteUserRequest(int Id);

    [HttpPost]
    public async Task<IActionResult> DeleteUser([FromBody] DeleteUserRequest req)
    {
        if (!User.IsInRole("Admin")) return Json(new { ok = false, error = "Nur Admins." });
        if (req == null || req.Id <= 0) return Json(new { ok = false, error = "Ungueltige Anfrage." });

        var ownIdStr = User.FindFirst("UserId")?.Value;
        if (int.TryParse(ownIdStr, out var ownId) && ownId == req.Id)
            return Json(new { ok = false, error = "Eigenes Konto kann nicht geloescht werden." });

        var target = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.Id);
        if (target == null) return Json(new { ok = false, error = "Benutzer nicht gefunden." });

        if (target.Rolle == "Admin")
        {
            var adminCount = await _db.Users.CountAsync(u => u.Rolle == "Admin");
            if (adminCount <= 1)
                return Json(new { ok = false, error = "Letzter Admin kann nicht geloescht werden." });
        }

        _db.Users.Remove(target);
        await _db.SaveChangesAsync();
        _log.LogWarning("Benutzer geloescht: {User} (Id={Id})", target.Benutzername, target.Id);
        return Json(new { ok = true });
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
