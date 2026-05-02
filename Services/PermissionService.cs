using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using UENTDispatcher.Models;

namespace UENTDispatcher.Services;

/// <summary>
/// Liest die Anwender-Berechtigungen aus AppSettings und liefert sie fuer den
/// aktuellen User. Admins kriegen immer alle Rechte; non-Admins (inkl. dem
/// anonymen "anwender"-Login) bekommen die in Settings konfigurierten Flags.
/// </summary>
public class PermissionService
{
    private readonly AppDbContext _db;
    public PermissionService(AppDbContext db) => _db = db;

    public async Task<UserPermissions> GetForAsync(ClaimsPrincipal user)
    {
        var istAdmin = user.IsInRole("Admin");
        if (istAdmin) return UserPermissions.AdminAlleRechte;

        var settings = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync();
        if (settings == null) return UserPermissions.AlleAus;

        return new UserPermissions
        {
            DarfDrehen = settings.UserDarfDrehen,
            DarfVerlaufSehen = settings.UserDarfVerlaufSehen,
            DarfTeilnehmendeAktiv = settings.UserDarfTeilnehmendeAktiv,
            DarfSperrlisteToggeln = settings.UserDarfSperrlisteToggeln,
            IstAdmin = false
        };
    }
}

public class UserPermissions
{
    public bool DarfDrehen { get; set; }
    public bool DarfVerlaufSehen { get; set; }
    public bool DarfTeilnehmendeAktiv { get; set; }
    public bool DarfSperrlisteToggeln { get; set; }
    public bool IstAdmin { get; set; }

    public static readonly UserPermissions AdminAlleRechte = new()
    {
        DarfDrehen = true,
        DarfVerlaufSehen = true,
        DarfTeilnehmendeAktiv = true,
        DarfSperrlisteToggeln = true,
        IstAdmin = true
    };

    public static readonly UserPermissions AlleAus = new();
}
