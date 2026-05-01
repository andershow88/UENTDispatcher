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
    public const int DefaultSperreTage = 21;

    private readonly AppDbContext _db;
    private readonly ILogger<DispatcherService> _log;
    private static readonly Random _rng = new();

    public DispatcherService(AppDbContext db, ILogger<DispatcherService> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>Liest die aktuelle Sperrfrist aus den AppSettings (Fallback 21).</summary>
    public async Task<int> GetSperreTageAsync(CancellationToken ct = default)
    {
        var s = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return s?.SperreTage ?? DefaultSperreTage;
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
    /// Persistiert eine bestaetigte Auswahl. Jede Bestaetigung erzeugt einen
    /// eigenen Verlaufseintrag mit frischer 21-Tage-Sperre — auch wenn die
    /// Person noch von einer fruehren Auswahl gesperrt war (Override-Fall).
    /// Mehrere Eintraege fuer dieselbe Person sind erlaubt; die effektive
    /// Sperre ergibt sich aus dem Eintrag mit dem juengsten SperrBisUtc.
    /// </summary>
    public async Task<ConfirmResult> ConfirmAsync(int employeeId, bool blacklistIgnoriert, CancellationToken ct = default)
    {
        var employee = await _db.Employees.FirstOrDefaultAsync(p => p.Id == employeeId && p.IstAktiv, ct);
        if (employee == null)
            return new ConfirmResult(false, "Mitarbeiter:in nicht gefunden oder inaktiv.", null);

        var now = DateTime.UtcNow;

        // Aktuell laufende Sperre (falls vorhanden) — nur fuer den Server-Check
        // bei fehlendem Override und fuer Audit-Logging benoetigt.
        var alteSperreUtc = await _db.DispatcherSelections
            .Where(s => s.EmployeeId == employeeId)
            .OrderByDescending(s => s.SperrBisUtc)
            .Select(s => (DateTime?)s.SperrBisUtc)
            .FirstOrDefaultAsync(ct);

        var nochGesperrt = alteSperreUtc.HasValue && alteSperreUtc.Value > now;
        var restTageVorher = nochGesperrt
            ? (int)Math.Ceiling((alteSperreUtc!.Value - now).TotalDays)
            : 0;

        // Sicherheitscheck: ohne Override darf ein gesperrter Mitarbeiter nicht
        // bestaetigt werden (Frontend sollte das schon verhindern, aber server-
        // seitig hart durchsetzen).
        if (nochGesperrt && !blacklistIgnoriert)
        {
            return new ConfirmResult(false,
                $"{employee.Anzeigename} ist noch {restTageVorher} Tag(e) gesperrt. " +
                "Bitte aktiviere 'Sperrliste ignorieren', wenn du diese Person trotzdem auswaehlen moechtest.",
                null);
        }

        // Frische Sperre laut aktueller Einstellung (Default 21 Tage) —
        // unabhaengig davon, ob die Person zuvor noch gesperrt war. Override-
        // Auswahlen erzeugen einen ZUSAETZLICHEN Eintrag im Verlauf; alte
        // Eintraege bleiben fuer die Historie erhalten.
        var sperreTage = await GetSperreTageAsync(ct);
        var neueSperreUtc = now.AddDays(sperreTage);

        var auswahl = new DispatcherSelection
        {
            EmployeeId = employeeId,
            BestaetigtUtc = now,
            SperrBisUtc = neueSperreUtc,
            BlacklistIgnoriert = blacklistIgnoriert,
            RestTageUebernommen = 0
        };
        _db.DispatcherSelections.Add(auswahl);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Dispatcher bestaetigt: {Name} (Id={Id}), Override={Override}, RestVorher={Rest} → SperrBis {SperrBis:O}",
            employee.Anzeigename, employee.Id, blacklistIgnoriert, restTageVorher, neueSperreUtc);

        return new ConfirmResult(true, null, new ConfirmInfo(
            EmployeeId: employee.Id,
            Anzeigename: employee.Anzeigename,
            BestaetigtUtc: now,
            SperrBisUtc: neueSperreUtc,
            RestTageUebernommen: 0,
            BlacklistIgnoriert: blacklistIgnoriert));
    }

    /// <summary>
    /// Loescht ALLE bestaetigten Auswahlen aus der DB. Damit ist auch jede
    /// laufende Sperre aufgehoben (Sperrstatus wird aus DispatcherSelections
    /// abgeleitet). Wird nur fuer Admin freigegeben.
    /// </summary>
    public async Task<int> ClearLogAsync(CancellationToken ct = default)
    {
        var count = await _db.DispatcherSelections.CountAsync(ct);
        if (count == 0) return 0;
        await _db.DispatcherSelections.ExecuteDeleteAsync(ct);
        _log.LogWarning("Dispatcher-Verlauf vollstaendig geloescht: {Count} Eintraege entfernt — alle Sperren sind aufgehoben.", count);
        return count;
    }

    /// <summary>
    /// Loescht einen einzelnen Verlaufseintrag. Wenn dadurch die zuletzt
    /// gespeicherte Sperre einer Person entfaellt, wird automatisch wieder
    /// auf den (ggf. juengsten verbleibenden) Eintrag fuer den Sperrstatus
    /// zurueckgegriffen.
    /// </summary>
    public async Task<DeleteEntryResult> DeleteEntryAsync(int id, CancellationToken ct = default)
    {
        var entry = await _db.DispatcherSelections
            .Include(s => s.Employee)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entry == null)
            return new DeleteEntryResult(false, "Eintrag nicht gefunden.");
        var name = entry.Employee?.Vorname + " " + entry.Employee?.Nachname;
        _db.DispatcherSelections.Remove(entry);
        await _db.SaveChangesAsync(ct);
        _log.LogWarning("Verlaufseintrag {Id} ({Name}, {Bestaetigt:O}) geloescht.", id, name, entry.BestaetigtUtc);
        return new DeleteEntryResult(true, null);
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
                s.Employee!.Vorname,           // Anzeige nur noch Vorname
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

public record DeleteEntryResult(bool Erfolgreich, string? Fehler);
