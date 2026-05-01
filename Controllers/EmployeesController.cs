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

    public record CreateRequest(string Vorname, string Nachname);

    [HttpPost("Create")]
    public async Task<IActionResult> Create([FromBody] CreateRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Vorname) || string.IsNullOrWhiteSpace(req.Nachname))
            return Json(new { ok = false, error = "Vor- und Nachname sind Pflicht." });

        var e = new Employee
        {
            Vorname = req.Vorname.Trim(),
            Nachname = req.Nachname.Trim(),
            IstAktiv = true,
            ErstelltAm = DateTime.UtcNow
        };
        _db.Employees.Add(e);
        await _db.SaveChangesAsync();
        return Json(new { ok = true, id = e.Id });
    }

    public record UpdateRequest(int Id, string Vorname, string Nachname, bool IstAktiv);

    [HttpPost("Update")]
    public async Task<IActionResult> Update([FromBody] UpdateRequest req)
    {
        if (req == null) return Json(new { ok = false, error = "Ungueltige Anfrage." });
        var e = await _db.Employees.FirstOrDefaultAsync(p => p.Id == req.Id);
        if (e == null) return Json(new { ok = false, error = "Mitarbeiter:in nicht gefunden." });
        if (string.IsNullOrWhiteSpace(req.Vorname) || string.IsNullOrWhiteSpace(req.Nachname))
            return Json(new { ok = false, error = "Vor- und Nachname sind Pflicht." });

        e.Vorname = req.Vorname.Trim();
        e.Nachname = req.Nachname.Trim();
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
}
