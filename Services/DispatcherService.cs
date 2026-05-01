using Microsoft.EntityFrameworkCore;
using UENTDispatcher.Models;

namespace UENTDispatcher.Services;

/// <summary>
/// Kernlogik des Dispatcher-Wheels: Zufallsauswahl, 21-Tage-Sperre,
/// Override-Mechanik mit kumulativer Sperrzeit. Wird in
/// <see cref="DispatcherController"/> und der Wheel-Seite verwendet.
/// </summary>
public class DispatcherService
{
    public const int SperreTage = 21;

    private readonly AppDbContext _db;
    private readonly ILogger<DispatcherService> _log;
    private static readonly Random _rng = new();

    public DispatcherService(AppDbContext db, ILogger<DispatcherService> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Liefert alle aktiven Mitarbeiter mit Sperr-Status. Frontend zeigt
    /// damit Verfuegbar / Gesperrt-Listen + Datum, ab wann jemand wieder darf.
    /// </summary>
    public async Task<List<EmployeeStatus>> ListEmployeeStatusesAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var employees = await _db.Employees
            .Where(p => p.IstAktiv)
            .OrderBy(p => p.Vorname).ThenBy(p => p.Nachname)
            .ToListAsync(ct);

        var lookup = await _db.DispatcherSelections
            .GroupBy(s => s.EmployeeId)
            .Select(g => new
            {
                EmployeeId = g.Key,
                LetzteSperreUtc = g.Max(s => s.SperrBisUtc)
            })
            .ToDictionaryAsync(x => x.EmployeeId, x => x.LetzteSperreUtc, ct);

        return employees.Select(p =>
        {
            lookup.TryGetValue(p.Id, out var sperreUtc);
            var gesperrt = sperreUtc > now;
            return new EmployeeStatus(
                p.Id,
                p.Vorname,
                p.Nachname,
                gesperrt,
                gesperrt ? sperreUtc : null,
                gesperrt ? Math.Max(1, (int)Math.Ceiling((sperreUtc - now).TotalDays)) : 0
            );
        }).ToList();
    }

    /// <summary>
    /// Waehlt zufaellig einen Kandidaten aus dem berechtigten Pool. Bei
    /// includeBlocked=true werden alle aktiven Mitarbeiter beruecksichtigt;
    /// sonst nur die aktuell nicht gesperrten.
    /// </summary>
    public async Task<SpinResult> SpinAsync(bool includeBlocked, CancellationToken ct = default)
    {
        var statuses = await ListEmployeeStatusesAsync(ct);
        var pool = includeBlocked
            ? statuses
            : statuses.Where(s => !s.Gesperrt).ToList();

        if (pool.Count == 0)
        {
            return new SpinResult(
                Erfolgreich: false,
                Fehler: includeBlocked
                    ? "Keine aktiven Mitarbeitenden vorhanden."
                    : "Alle aktiven Mitarbeitenden sind aktuell gesperrt. Bitte 'Sperrliste ignorieren' aktivieren.",
                Auswahl: null,
                AlleKandidaten: statuses);
        }

        var winner = pool[_rng.Next(pool.Count)];
        return new SpinResult(
            Erfolgreich: true,
            Fehler: null,
            Auswahl: winner,
            AlleKandidaten: statuses);
    }

    /// <summary>
    /// Persistiert eine bestaetigte Auswahl mit korrekter Sperr-Berechnung.
    /// Wenn der Mitarbeiter aktuell noch gesperrt ist (nur moeglich bei
    /// blacklistIgnoriert=true), wird die Restsperre auf die neuen 21 Tage
    /// aufgeschlagen — d. h. SperrBisUtc = max(jetzt, alteSperre) + 21 Tage.
    /// </summary>
    public async Task<ConfirmResult> ConfirmAsync(int employeeId, bool blacklistIgnoriert, CancellationToken ct = default)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(p => p.Id == employeeId && p.IstAktiv, ct);
        if (employee == null)
            return new ConfirmResult(false, "Mitarbeiter:in nicht gefunden oder inaktiv.", null);

        var now = DateTime.UtcNow;
        var alteSperreUtc = await _db.DispatcherSelections
            .Where(s => s.EmployeeId == employeeId)
            .OrderByDescending(s => s.SperrBisUtc)
            .Select(s => (DateTime?)s.SperrBisUtc)
            .FirstOrDefaultAsync(ct);

        var basis = (alteSperreUtc.HasValue && alteSperreUtc.Value > now) ? alteSperreUtc.Value : now;
        var neueSperreUtc = basis.AddDays(SperreTage);
        var restTage = (alteSperreUtc.HasValue && alteSperreUtc.Value > now)
            ? (int)Math.Ceiling((alteSperreUtc.Value - now).TotalDays)
            : 0;

        // Sicherheitscheck: ohne Override darf ein gesperrter Mitarbeiter nicht
        // bestaetigt werden (Frontend sollte das schon verhindern, aber server-
        // seitig hart durchsetzen).
        if (restTage > 0 && !blacklistIgnoriert)
        {
            return new ConfirmResult(false,
                $"{employee.Anzeigename} ist noch {restTage} Tag(e) gesperrt. " +
                "Bitte aktiviere 'Sperrliste ignorieren', wenn du diese Person trotzdem auswaehlen moechtest.",
                null);
        }

        var auswahl = new DispatcherSelection
        {
            EmployeeId = employeeId,
            BestaetigtUtc = now,
            SperrBisUtc = neueSperreUtc,
            BlacklistIgnoriert = blacklistIgnoriert,
            RestTageUebernommen = restTage
        };
        _db.DispatcherSelections.Add(auswahl);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Dispatcher bestaetigt: {Name} (Id={Id}), Override={Override}, Rest={Rest} → SperrBis {SperrBis:O}",
            employee.Anzeigename, employee.Id, blacklistIgnoriert, restTage, neueSperreUtc);

        return new ConfirmResult(true, null, new ConfirmInfo(
            EmployeeId: employee.Id,
            Anzeigename: employee.Anzeigename,
            BestaetigtUtc: now,
            SperrBisUtc: neueSperreUtc,
            RestTageUebernommen: restTage,
            BlacklistIgnoriert: blacklistIgnoriert));
    }

    /// <summary>Bisherige bestaetigte Auswahlen — neueste zuerst.</summary>
    public async Task<List<LogEintrag>> ListLogAsync(int max = 200, CancellationToken ct = default)
    {
        return await _db.DispatcherSelections
            .Include(s => s.Employee)
            .OrderByDescending(s => s.BestaetigtUtc)
            .Take(max)
            .Select(s => new LogEintrag(
                s.Id,
                s.Employee!.Vorname + " " + s.Employee.Nachname,
                s.BestaetigtUtc,
                s.SperrBisUtc,
                s.BlacklistIgnoriert,
                s.RestTageUebernommen))
            .ToListAsync(ct);
    }
}

public record EmployeeStatus(
    int Id,
    string Vorname,
    string Nachname,
    bool Gesperrt,
    DateTime? SperrBisUtc,
    int RestTage);

public record SpinResult(
    bool Erfolgreich,
    string? Fehler,
    EmployeeStatus? Auswahl,
    List<EmployeeStatus> AlleKandidaten);

public record ConfirmResult(
    bool Erfolgreich,
    string? Fehler,
    ConfirmInfo? Info);

public record ConfirmInfo(
    int EmployeeId,
    string Anzeigename,
    DateTime BestaetigtUtc,
    DateTime SperrBisUtc,
    int RestTageUebernommen,
    bool BlacklistIgnoriert);

public record LogEintrag(
    int Id,
    string Anzeigename,
    DateTime BestaetigtUtc,
    DateTime SperrBisUtc,
    bool BlacklistIgnoriert,
    int RestTageUebernommen);
