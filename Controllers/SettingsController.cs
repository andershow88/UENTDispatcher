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

    public record UpdatePermissionsRequest(
        bool UserDarfDrehen,
        bool UserDarfVerlaufSehen,
        bool UserDarfTeilnehmendeAktiv,
        bool UserDarfSperrlisteToggeln);

    /// <summary>Berechtigungen fuer die Rolle "User" (inkl. anonymem Anwender)
    /// als Bool-Flags speichern. Default = alles aus.</summary>
    [HttpPost]
    public async Task<IActionResult> UpdatePermissions([FromBody] UpdatePermissionsRequest req)
    {
        if (!User.IsInRole("Admin")) return Json(new { ok = false, error = "Nur Admins." });
        if (req == null) return Json(new { ok = false, error = "Ungueltige Anfrage." });

        var settings = await _db.AppSettings.FirstOrDefaultAsync();
        if (settings == null)
        {
            settings = new AppSettings { Id = 1, SperreTage = 21 };
            _db.AppSettings.Add(settings);
        }
        settings.UserDarfDrehen = req.UserDarfDrehen;
        settings.UserDarfVerlaufSehen = req.UserDarfVerlaufSehen;
        settings.UserDarfTeilnehmendeAktiv = req.UserDarfTeilnehmendeAktiv;
        settings.UserDarfSperrlisteToggeln = req.UserDarfSperrlisteToggeln;
        settings.ZuletztGeaendertUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _log.LogInformation("Anwender-Berechtigungen aktualisiert: Drehen={D}, Verlauf={V}, Teilnehmende={T}, Sperrliste={S}",
            req.UserDarfDrehen, req.UserDarfVerlaufSehen, req.UserDarfTeilnehmendeAktiv, req.UserDarfSperrlisteToggeln);
        return Json(new { ok = true });
    }

    // ── Benutzerverwaltung ──────────────────────────────────────────────

    public record CreateUserRequest(string Benutzername, string Anzeigename, string Rolle);

    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
    {
        if (!User.IsInRole("Admin")) return Json(new { ok = false, error = "Nur Admins." });
        if (req == null) return Json(new { ok = false, error = "Ungueltige Anfrage." });
        var benutzername = (req.Benutzername ?? "").Trim();
        var anzeigename = (req.Anzeigename ?? "").Trim();
        var rolle = (req.Rolle ?? "").Trim();

        if (string.IsNullOrEmpty(benutzername))
            return Json(new { ok = false, error = "Benutzername ist Pflicht." });
        if (benutzername.Length > 100)
            return Json(new { ok = false, error = "Benutzername ist zu lang (max. 100)." });
        if (string.IsNullOrEmpty(anzeigename))
            return Json(new { ok = false, error = "Anzeigename ist Pflicht." });
        if (rolle != "Admin" && rolle != "User")
            return Json(new { ok = false, error = "Rolle muss 'Admin' oder 'User' sein." });

        var exists = await _db.Users.AnyAsync(u => u.Benutzername == benutzername);
        if (exists) return Json(new { ok = false, error = "Benutzername ist bereits vergeben." });

        // Setup-Token generieren — User setzt das Passwort selbst ueber den Link.
        // Bis dahin: IstAktiv=false (Konto kann nicht eingeloggt werden).
        var token = GenerateUrlToken(32);
        var u = new AppUser
        {
            Benutzername = benutzername,
            Anzeigename = anzeigename,
            // Platzhalter-Hash — wird vom User per Setup-Link ueberschrieben.
            // Random unguessable, damit niemand mit Leerpasswort durchkommt.
            PasswortHash = Hash(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")),
            Rolle = rolle,
            IstAktiv = false,
            ErstelltAm = DateTime.UtcNow,
            EinrichtungsToken = token,
            EinrichtungsTokenAblaufUtc = DateTime.UtcNow.AddDays(7)
        };
        _db.Users.Add(u);
        await _db.SaveChangesAsync();

        var setupUrl = Url.Action("Einrichten", "Account", new { token }, Request.Scheme, Request.Host.Value);
        _log.LogInformation("Neuer Benutzer angelegt: {User} (Rolle={Rolle}), Setup-Link generiert (gueltig bis {Ablauf:O})",
            benutzername, rolle, u.EinrichtungsTokenAblaufUtc);
        return Json(new
        {
            ok = true,
            id = u.Id,
            setupUrl,
            ablaufUtc = u.EinrichtungsTokenAblaufUtc
        });
    }

    public record RegenerateLinkRequest(int Id);

    /// <summary>Erzeugt einen frischen Setup-Link fuer einen User mit ausstehender
    /// Einrichtung. Bestehende Tokens werden invalidiert.</summary>
    [HttpPost]
    public async Task<IActionResult> RegenerateUserLink([FromBody] RegenerateLinkRequest req)
    {
        if (!User.IsInRole("Admin")) return Json(new { ok = false, error = "Nur Admins." });
        if (req == null || req.Id <= 0) return Json(new { ok = false, error = "Ungueltige Anfrage." });

        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == req.Id);
        if (u == null) return Json(new { ok = false, error = "Benutzer nicht gefunden." });

        var token = GenerateUrlToken(32);
        u.EinrichtungsToken = token;
        u.EinrichtungsTokenAblaufUtc = DateTime.UtcNow.AddDays(7);
        u.IstAktiv = false; // bis User Passwort setzt, gesperrt
        await _db.SaveChangesAsync();

        var setupUrl = Url.Action("Einrichten", "Account", new { token }, Request.Scheme, Request.Host.Value);
        _log.LogInformation("Setup-Link erneut generiert fuer {User}", u.Benutzername);
        return Json(new { ok = true, setupUrl, ablaufUtc = u.EinrichtungsTokenAblaufUtc });
    }

    private static string GenerateUrlToken(int byteLength)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public record UpdateOwnAccountRequest(string Benutzername, string Anzeigename, string? NeuesPasswort);

    /// <summary>
    /// Eigenes Admin-Konto bearbeiten — Benutzername, Anzeigename und optional
    /// das Passwort. NeuesPasswort leer/null lassen → Passwort bleibt unveraendert.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> UpdateOwnAccount([FromBody] UpdateOwnAccountRequest req)
    {
        if (!User.IsInRole("Admin")) return Json(new { ok = false, error = "Nur Admins." });
        if (req == null) return Json(new { ok = false, error = "Ungueltige Anfrage." });

        var ownIdStr = User.FindFirst("UserId")?.Value;
        if (!int.TryParse(ownIdStr, out var ownId) || ownId <= 0)
            return Json(new { ok = false, error = "Eigene UserId nicht ermittelbar." });

        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == ownId);
        if (u == null) return Json(new { ok = false, error = "Eigenes Konto nicht gefunden." });

        var benutzername = (req.Benutzername ?? "").Trim();
        var anzeigename = (req.Anzeigename ?? "").Trim();
        if (string.IsNullOrEmpty(benutzername))
            return Json(new { ok = false, error = "Benutzername ist Pflicht." });
        if (benutzername.Length > 100)
            return Json(new { ok = false, error = "Benutzername ist zu lang (max. 100)." });
        if (string.IsNullOrEmpty(anzeigename))
            return Json(new { ok = false, error = "Anzeigename ist Pflicht." });

        // Username-Eindeutigkeit: nur pruefen, wenn er sich aendert
        if (!string.Equals(benutzername, u.Benutzername, StringComparison.Ordinal))
        {
            var taken = await _db.Users.AnyAsync(x => x.Id != ownId && x.Benutzername == benutzername);
            if (taken) return Json(new { ok = false, error = "Benutzername ist bereits vergeben." });
        }

        u.Benutzername = benutzername;
        u.Anzeigename = anzeigename;

        var passwortGeaendert = false;
        if (!string.IsNullOrEmpty(req.NeuesPasswort))
        {
            if (req.NeuesPasswort.Length < 4 || req.NeuesPasswort.Length > 200)
                return Json(new { ok = false, error = "Neues Passwort muss zwischen 4 und 200 Zeichen sein." });
            u.PasswortHash = Hash(req.NeuesPasswort);
            passwortGeaendert = true;
        }

        await _db.SaveChangesAsync();
        _log.LogInformation("Admin-Konto aktualisiert: Id={Id}, Benutzername={User}, PasswortGeaendert={PwdChanged}",
            u.Id, u.Benutzername, passwortGeaendert);

        return Json(new
        {
            ok = true,
            passwortGeaendert,
            // Hinweis: bei geaendertem Passwort sollte sich der User neu anmelden,
            // da der bestehende Cookie noch die alten Claims enthaelt.
            reloginEmpfohlen = passwortGeaendert
        });
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
