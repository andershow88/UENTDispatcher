using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using UENTDispatcher.Models;
using UENTDispatcher.Services;

// Npgsql 6+ verlangt sonst Kind=Utc fuer "timestamp with time zone".
// Wir speichern bewusst UTC und vermeiden so subtile Timezone-Verschiebungen.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddScoped<DispatcherService>();

// ── Datenbank: 1. Postgres via DATABASE_URL  2. SQLite im DATA_DIR  3. lokales SQLite ──
//
// Persistenz auf Railway:
//  - Postgres-Plugin → DATABASE_URL wird automatisch gesetzt → Daten persistent
//  - Sonst Volume mounten und Env-Variable DATA_DIR auf den Pfad setzen
//    → SQLite landet in ${DATA_DIR}/uentdispatcher.db und ueberlebt Deploys
//  - Ohne beides liegt die SQLite im Container-FS und ist bei jedem Deploy weg
var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
var dataDir = Environment.GetEnvironmentVariable("DATA_DIR");
var laufImContainer = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PORT"));
string dbBeschreibung;
bool dbPersistentVermutet;

if (!string.IsNullOrWhiteSpace(dbUrl))
{
    var cs = ParseRailwayPostgresUrl(dbUrl);
    builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs));
    var pgUri = new Uri(dbUrl);
    dbBeschreibung = $"Postgres (Host={pgUri.Host}, DB={pgUri.AbsolutePath.TrimStart('/')})";
    dbPersistentVermutet = true;
}
else
{
    string sqlitePath;
    if (!string.IsNullOrWhiteSpace(dataDir) && Directory.Exists(dataDir))
    {
        sqlitePath = Path.Combine(dataDir, "uentdispatcher.db");
        dbBeschreibung = $"SQLite (DATA_DIR={dataDir}/uentdispatcher.db) — Volume erwartet";
        dbPersistentVermutet = true;
    }
    else
    {
        sqlitePath = Path.Combine(builder.Environment.ContentRootPath, "uentdispatcher.db");
        dbBeschreibung = $"SQLite (ContentRoot/uentdispatcher.db)";
        dbPersistentVermutet = !laufImContainer;
    }
    builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={sqlitePath}"));
}

// ── Cookie-Authentifizierung ───────────────────────────────────────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.LogoutPath = "/Account/Logout";
        o.ExpireTimeSpan = TimeSpan.FromHours(10);
        o.SlidingExpiration = true;
        o.Cookie.Name = "UENTDispatcher.Auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
    });

builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

app.UseDeveloperExceptionPage();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// ── DB sicherstellen + Seed ────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        log.LogInformation("=== UENTDispatcher V1 startet ===");
        log.LogInformation("Datenbank: {Beschreibung}", dbBeschreibung);
        if (!dbPersistentVermutet)
        {
            log.LogWarning(
                "ACHTUNG: Datenbank liegt im Container-Filesystem — alle Daten gehen beim naechsten Deploy verloren. " +
                "Loesung: Postgres-Plugin in Railway aktivieren (setzt DATABASE_URL) ODER ein Volume mounten und Env-Variable " +
                "DATA_DIR auf den Mount-Pfad setzen.");
        }

        await db.Database.EnsureCreatedAsync();

        // Schema-Heilung: nachtraeglich hinzugefuegte Spalten in bereits
        // bestehenden DBs ergaenzen (EnsureCreated migriert solche
        // Aenderungen nicht). Foto-Spalten fuer Employees ab V1.1.
        await TryEnsureColumnsAsync(db, log);

        if (!await db.Users.AnyAsync())
        {
            db.Users.AddRange(
                new AppUser
                {
                    Benutzername = "admin",
                    PasswortHash = Hash("Admin1234!"),
                    Anzeigename = "Administrator",
                    Rolle = "Admin",
                    IstAktiv = true
                },
                new AppUser
                {
                    Benutzername = "user",
                    PasswortHash = Hash("User1234!"),
                    Anzeigename = "Demo Anwender",
                    Rolle = "User",
                    IstAktiv = true
                });
            await db.SaveChangesAsync();
            log.LogInformation("Standard-Benutzer angelegt: admin / user");
        }

        if (!await db.Employees.AnyAsync())
        {
            // Beispiel-Mitarbeiter fuer den Erststart — Pflege via /Employees-Seite
            // (Admin-Login). Beliebig editierbar/loeschbar nach dem ersten Login.
            db.Employees.AddRange(
                new Employee { Vorname = "Anna", Nachname = "Beispiel", IstAktiv = true },
                new Employee { Vorname = "Ben", Nachname = "Demo", IstAktiv = true },
                new Employee { Vorname = "Carla", Nachname = "Muster", IstAktiv = true },
                new Employee { Vorname = "Daniel", Nachname = "Test", IstAktiv = true },
                new Employee { Vorname = "Eva", Nachname = "Probe", IstAktiv = true },
                new Employee { Vorname = "Felix", Nachname = "Pilot", IstAktiv = true });
            await db.SaveChangesAsync();
            log.LogInformation("Beispiel-Mitarbeiter angelegt (6 Personen).");
        }
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Datenbank-Initialisierung fehlgeschlagen");
    }
}

app.Run();

static async Task TryEnsureColumnsAsync(AppDbContext db, ILogger log)
{
    var provider = db.Database.ProviderName ?? "";
    var isPg = provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);

    var pgStmts = new[]
    {
        "ALTER TABLE \"Employees\" ADD COLUMN IF NOT EXISTS \"PhotoBytes\" bytea NULL",
        "ALTER TABLE \"Employees\" ADD COLUMN IF NOT EXISTS \"PhotoMimeType\" varchar(50) NULL"
    };
    var sqliteStmts = new[]
    {
        "ALTER TABLE \"Employees\" ADD COLUMN \"PhotoBytes\" BLOB NULL",
        "ALTER TABLE \"Employees\" ADD COLUMN \"PhotoMimeType\" TEXT NULL"
    };

    foreach (var sql in isPg ? pgStmts : sqliteStmts)
    {
        try { await db.Database.ExecuteSqlRawAsync(sql); }
        catch (Exception ex)
        {
            // SQLite kennt kein IF NOT EXISTS — "duplicate column" ignorieren
            if (!isPg && ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) continue;
            log.LogDebug(ex, "Schema-Heilung uebersprungen fuer: {Sql}", sql);
        }
    }
}

static string ParseRailwayPostgresUrl(string url)
{
    var uri = new Uri(url);
    var userInfo = uri.UserInfo.Split(':', 2);
    var dbName = uri.AbsolutePath.TrimStart('/');
    return $"Host={uri.Host};Port={(uri.Port > 0 ? uri.Port : 5432)};Username={userInfo[0]};Password={userInfo.ElementAtOrDefault(1)};Database={dbName};SSL Mode=Require;Trust Server Certificate=true;";
}

static string Hash(string s) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
