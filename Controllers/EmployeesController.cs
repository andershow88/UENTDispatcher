using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UENTDispatcher.Models;

namespace UENTDispatcher.Controllers;

/// <summary>
/// CRUD fuer die Mitarbeitendenliste. In V1 darf jede:r angemeldete User
/// pflegen — bei Bedarf spaeter auf Admin einschraenken via [Authorize(Roles="Admin")].
/// </summary>
[Route("[controller]")]
public class EmployeesController : Controller
{
    private readonly AppDbContext _db;
    public EmployeesController(AppDbContext db) => _db = db;

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var list = await _db.Employees
            .OrderBy(p => p.Vorname).ThenBy(p => p.Nachname)
            .ToListAsync();
        return View(list);
    }

    public record CreateRequest(string Vorname);

    [HttpPost("Create")]
    public async Task<IActionResult> Create([FromBody] CreateRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Vorname))
            return Json(new { ok = false, error = "Vorname ist Pflicht." });

        var e = new Employee
        {
            Vorname = req.Vorname.Trim(),
            Nachname = string.Empty,        // Nachname wird nicht mehr gepflegt
            IstAktiv = true,
            ErstelltAm = DateTime.UtcNow
        };
        _db.Employees.Add(e);
        await _db.SaveChangesAsync();
        return Json(new { ok = true, id = e.Id });
    }

    public record UpdateRequest(int Id, string Vorname, bool IstAktiv);

    [HttpPost("Update")]
    public async Task<IActionResult> Update([FromBody] UpdateRequest req)
    {
        if (req == null) return Json(new { ok = false, error = "Ungueltige Anfrage." });
        var e = await _db.Employees.FirstOrDefaultAsync(p => p.Id == req.Id);
        if (e == null) return Json(new { ok = false, error = "Mitarbeiter:in nicht gefunden." });
        if (string.IsNullOrWhiteSpace(req.Vorname))
            return Json(new { ok = false, error = "Vorname ist Pflicht." });

        e.Vorname = req.Vorname.Trim();
        e.IstAktiv = req.IstAktiv;
        await _db.SaveChangesAsync();
        return Json(new { ok = true });
    }

    public record DeleteRequest(int Id);

    [HttpPost("Delete")]
    public async Task<IActionResult> Delete([FromBody] DeleteRequest req)
    {
        if (req == null || req.Id <= 0) return Json(new { ok = false, error = "Ungueltige Anfrage." });
        var e = await _db.Employees.FirstOrDefaultAsync(p => p.Id == req.Id);
        if (e == null) return Json(new { ok = false, error = "Mitarbeiter:in nicht gefunden." });
        // Cascading Delete entfernt auch die Auswahl-Eintraege.
        _db.Employees.Remove(e);
        await _db.SaveChangesAsync();
        return Json(new { ok = true });
    }

    public record ToggleActiveRequest(int Id, bool IstAktiv);

    /// <summary>Aktiv-Flag direkt aus der Liste umschalten — ohne Modal-Edit.</summary>
    [HttpPost("ToggleActive")]
    public async Task<IActionResult> ToggleActive([FromBody] ToggleActiveRequest req)
    {
        if (req == null || req.Id <= 0) return Json(new { ok = false, error = "Ungueltige Anfrage." });
        var e = await _db.Employees.FirstOrDefaultAsync(p => p.Id == req.Id);
        if (e == null) return Json(new { ok = false, error = "Mitarbeiter:in nicht gefunden." });
        e.IstAktiv = req.IstAktiv;
        await _db.SaveChangesAsync();
        return Json(new { ok = true });
    }

    private static readonly string[] AllowedPhotoTypes = { "image/jpeg", "image/png", "image/webp", "image/gif" };
    private const long MaxPhotoBytes = 5 * 1024 * 1024; // 5 MB

    /// <summary>Foto-Upload (multipart). Max 5 MB, JPEG/PNG/WebP/GIF.</summary>
    [HttpPost("UploadPhoto")]
    [RequestSizeLimit(MaxPhotoBytes + 1024 * 1024)]
    public async Task<IActionResult> UploadPhoto([FromForm] int id, IFormFile? file)
    {
        if (id <= 0) return Json(new { ok = false, error = "Ungueltige ID." });
        if (file == null || file.Length == 0) return Json(new { ok = false, error = "Kein Foto ausgewaehlt." });
        if (file.Length > MaxPhotoBytes) return Json(new { ok = false, error = "Foto zu gross (max. 5 MB)." });
        var contentType = (file.ContentType ?? "").ToLowerInvariant();
        if (!AllowedPhotoTypes.Contains(contentType))
            return Json(new { ok = false, error = "Nicht unterstuetzter Dateityp. Erlaubt: JPEG, PNG, WebP, GIF." });

        var e = await _db.Employees.FirstOrDefaultAsync(p => p.Id == id);
        if (e == null) return Json(new { ok = false, error = "Mitarbeiter:in nicht gefunden." });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        e.PhotoBytes = ms.ToArray();
        e.PhotoMimeType = contentType;
        await _db.SaveChangesAsync();
        return Json(new { ok = true });
    }

    /// <summary>Foto eines Mitarbeiters ausliefern. 1h Browser-Cache.</summary>
    [HttpGet("Photo/{id:int}")]
    public async Task<IActionResult> Photo(int id)
    {
        var data = await _db.Employees
            .Where(p => p.Id == id)
            .Select(p => new { p.PhotoBytes, p.PhotoMimeType })
            .FirstOrDefaultAsync();
        if (data?.PhotoBytes == null || data.PhotoBytes.Length == 0) return NotFound();
        Response.Headers["Cache-Control"] = "private, max-age=3600";
        return File(data.PhotoBytes, data.PhotoMimeType ?? "image/jpeg");
    }

    public record DeletePhotoRequest(int Id);

    [HttpPost("DeletePhoto")]
    public async Task<IActionResult> DeletePhoto([FromBody] DeletePhotoRequest req)
    {
        if (req == null || req.Id <= 0) return Json(new { ok = false, error = "Ungueltige Anfrage." });
        var e = await _db.Employees.FirstOrDefaultAsync(p => p.Id == req.Id);
        if (e == null) return Json(new { ok = false, error = "Mitarbeiter:in nicht gefunden." });
        e.PhotoBytes = null;
        e.PhotoMimeType = null;
        await _db.SaveChangesAsync();
        return Json(new { ok = true });
    }
}
