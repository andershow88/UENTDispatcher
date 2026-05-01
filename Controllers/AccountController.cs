using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UENTDispatcher.Models;

namespace UENTDispatcher.Controllers;

[AllowAnonymous]
public class AccountController : Controller
{
    private readonly AppDbContext _db;
    private readonly ILogger<AccountController> _log;

    public AccountController(AppDbContext db, ILogger<AccountController> log)
    {
        _db = db;
        _log = log;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
        ViewBag.ReturnUrl = returnUrl;
        return View();
    }

    public record LoginRequest(string Benutzername, string Passwort);

    [HttpPost]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Benutzername) || string.IsNullOrWhiteSpace(req?.Passwort))
            return Json(new { ok = false, error = "Bitte Benutzername und Passwort eingeben." });

        var hash = Hash(req.Passwort);
        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.Benutzername == req.Benutzername && u.PasswortHash == hash && u.IstAktiv);
        if (user is null)
        {
            _log.LogWarning("Login fehlgeschlagen fuer {User}", req.Benutzername);
            return Json(new { ok = false, error = "Benutzername oder Passwort ungueltig." });
        }

        var claims = new List<Claim>
        {
            new("UserId", user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Benutzername),
            new(ClaimTypes.GivenName, user.Anzeigename),
            new(ClaimTypes.Role, user.Rolle)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        return Json(new { ok = true });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    /// <summary>
    /// Click-only User-Login. Erzeugt eine ephemere Identitaet mit Rolle "User"
    /// — kein DB-User, keine Credentials. Gleiche Berechtigungen wie ein
    /// regulaerer User: alles sichtbar, nichts aenderbar.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> UserLogin()
    {
        var claims = new List<Claim>
        {
            new("UserId", "0"),                                // synthetisch, kein DB-Bezug
            new(ClaimTypes.NameIdentifier, "0"),
            new(ClaimTypes.Name, "anwender"),
            new(ClaimTypes.GivenName, "Anwender"),
            new(ClaimTypes.Role, "User")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
        return Json(new { ok = true });
    }

    // ── Setup-Flow: User legt sein Passwort via Link selbst fest ─────────────

    [HttpGet]
    public async Task<IActionResult> Einrichten(string? token)
    {
        var user = await FindUserByValidToken(token);
        if (user == null)
        {
            ViewBag.Fehler = "Der Einrichtungs-Link ist ungueltig oder abgelaufen. Bitte neuen Link beim Admin anfordern.";
            return View();
        }
        ViewBag.Benutzername = user.Benutzername;
        ViewBag.Anzeigename = user.Anzeigename;
        ViewBag.Rolle = user.Rolle;
        ViewBag.Token = token;
        return View();
    }

    public record EinrichtenRequest(string Token, string Passwort, string Bestaetigung);

    [HttpPost]
    public async Task<IActionResult> EinrichtenSpeichern([FromBody] EinrichtenRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Token))
            return Json(new { ok = false, error = "Ungueltige Anfrage." });

        var user = await FindUserByValidToken(req.Token);
        if (user == null)
            return Json(new { ok = false, error = "Der Einrichtungs-Link ist ungueltig oder abgelaufen." });

        if (string.IsNullOrEmpty(req.Passwort) || req.Passwort.Length < 4)
            return Json(new { ok = false, error = "Das Passwort muss mindestens 4 Zeichen lang sein." });
        if (req.Passwort.Length > 200)
            return Json(new { ok = false, error = "Passwort ist zu lang." });
        if (req.Passwort != req.Bestaetigung)
            return Json(new { ok = false, error = "Die beiden Passwoerter stimmen nicht ueberein." });

        user.PasswortHash = Hash(req.Passwort);
        user.IstAktiv = true;
        user.EinrichtungsToken = null;
        user.EinrichtungsTokenAblaufUtc = null;
        await _db.SaveChangesAsync();

        _log.LogInformation("Konto-Einrichtung abgeschlossen fuer {User}", user.Benutzername);
        return Json(new { ok = true });
    }

    private async Task<AppUser?> FindUserByValidToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 16 or > 200) return null;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.EinrichtungsToken == token);
        if (user == null) return null;
        if (user.EinrichtungsTokenAblaufUtc != null && user.EinrichtungsTokenAblaufUtc <= DateTime.UtcNow)
            return null;
        return user;
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
