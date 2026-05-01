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

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
