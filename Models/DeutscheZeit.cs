namespace UENTDispatcher.Models;

/// <summary>
/// Zentrale Zeitumrechnung in die deutsche Zeitzone (Europe/Berlin).
/// In der DB werden Zeiten konsequent als UTC gespeichert; fuer Anzeige und
/// "jetzt"-Werte nutzen wir diese Helfer, damit der Server-Container (UTC)
/// nicht durchschlaegt und Sommer-/Winterzeit korrekt beruecksichtigt wird.
/// </summary>
public static class DeutscheZeit
{
    private static readonly TimeZoneInfo BerlinTz = ResolveBerlinTimeZone();

    private static TimeZoneInfo ResolveBerlinTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        try { return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        return TimeZoneInfo.Utc;
    }

    public static DateTime ToLokal(DateTime zeitstempel)
    {
        var utc = zeitstempel.Kind switch
        {
            DateTimeKind.Local => zeitstempel.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(zeitstempel, DateTimeKind.Utc),
            _ => zeitstempel
        };
        return TimeZoneInfo.ConvertTimeFromUtc(utc, BerlinTz);
    }

    public static DateTime Jetzt => ToLokal(DateTime.UtcNow);
    public static DateTime Heute => Jetzt.Date;
}
