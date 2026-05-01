using Microsoft.EntityFrameworkCore;

namespace UENTDispatcher.Models;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<DispatcherSelection> DispatcherSelections => Set<DispatcherSelection>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);

        mb.Entity<AppUser>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Benutzername).IsUnique();
            e.Property(u => u.Benutzername).HasMaxLength(100).IsRequired();
            e.Property(u => u.PasswortHash).IsRequired();
            e.Property(u => u.Anzeigename).HasMaxLength(200);
            e.Property(u => u.Rolle).HasMaxLength(20);
        });

        mb.Entity<Employee>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Vorname).HasMaxLength(100).IsRequired();
            e.Property(p => p.Nachname).HasMaxLength(100).IsRequired();
            e.Property(p => p.PhotoMimeType).HasMaxLength(50);
            e.HasIndex(p => p.IstAktiv);
        });

        mb.Entity<DispatcherSelection>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasOne(s => s.Employee)
                .WithMany(p => p.Auswahlen)
                .HasForeignKey(s => s.EmployeeId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.BestaetigtUtc);
            e.HasIndex(s => s.SperrBisUtc);
        });

        mb.Entity<AppSettings>(e =>
        {
            e.HasKey(s => s.Id);
        });
    }
}

/// <summary>
/// Globale Einstellungen der Anwendung. Eine Tabelle mit nur einem Datensatz
/// (Id=1). Wird beim Start angelegt, falls noch nicht vorhanden.
/// </summary>
public class AppSettings
{
    public int Id { get; set; } = 1;

    /// <summary>Sperrfrist in Tagen — Default 21.</summary>
    public int SperreTage { get; set; } = 21;

    public DateTime ZuletztGeaendertUtc { get; set; } = DateTime.UtcNow;
}

public class AppUser
{
    public int Id { get; set; }
    public string Benutzername { get; set; } = string.Empty;
    public string PasswortHash { get; set; } = string.Empty;
    public string Anzeigename { get; set; } = string.Empty;
    public string Rolle { get; set; } = "User"; // "Admin" | "User"
    public bool IstAktiv { get; set; } = true;
    public DateTime ErstelltAm { get; set; } = DateTime.UtcNow;
}

public class Employee
{
    public int Id { get; set; }
    public string Vorname { get; set; } = string.Empty;
    public string Nachname { get; set; } = string.Empty;
    public bool IstAktiv { get; set; } = true;
    public DateTime ErstelltAm { get; set; } = DateTime.UtcNow;

    /// <summary>Profilbild als Bytes — Null wenn kein Foto hinterlegt.</summary>
    public byte[]? PhotoBytes { get; set; }
    /// <summary>MIME-Type des Fotos (z. B. image/jpeg).</summary>
    public string? PhotoMimeType { get; set; }

    public List<DispatcherSelection> Auswahlen { get; set; } = new();

    /// <summary>Anzeige-Name fuer UI: nur Vorname (Nachname wird intern
    /// gespeichert, aber nirgendwo mehr angezeigt — V1.2).</summary>
    public string Anzeigename => (Vorname ?? string.Empty).Trim();
}

/// <summary>
/// Eine bestaetigte Dispatcher-Auswahl. Nur bestaetigte Spins landen hier.
/// SperrBisUtc bestimmt die 21-Tage-Sperre — bei Override-Auswahl wird die
/// noch laufende Restsperre kumulativ angerechnet (siehe DispatcherService).
/// </summary>
public class DispatcherSelection
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    /// <summary>Zeitpunkt der Bestaetigung (UTC).</summary>
    public DateTime BestaetigtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Sperre laeuft bis zu diesem UTC-Zeitpunkt. Solange &gt; jetzt → gesperrt.</summary>
    public DateTime SperrBisUtc { get; set; }

    /// <summary>Wurde die Sperrliste bei dieser Auswahl ignoriert?</summary>
    public bool BlacklistIgnoriert { get; set; }

    /// <summary>Resttage der vorherigen Sperre, die bei Override aufgeschlagen wurden (0 = keine).</summary>
    public int RestTageUebernommen { get; set; }
}
