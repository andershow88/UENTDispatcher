# UENT Dispatcher

Wöchentliche Auslosung des Service-Desk-Dispatchers für den Jour fixe der Abteilung — als moderne, spielerische Web-App mit animiertem Glücksrad, 21-Tage-Sperrlogik und Verlaufsprotokoll.

## Tech-Stack

Übernommen 1:1 aus [TeamsMB](https://github.com/andershow88/TeamsMB):

- **ASP.NET Core 8 MVC** (`Microsoft.NET.Sdk.Web`)
- **EF Core 8** mit Postgres (Railway Default) + SQLite-Fallback (lokal/Volume)
- **Cookie-Auth** mit Standard-Usern (`admin` / `Admin1234!`, `user` / `User1234!`)
- **Bootstrap 5** + **Bootstrap Icons** + **Inter** (Google Fonts)
- **Dockerfile + railway.toml** — direkt deploybar auf Railway
- Bank-CI: Petrol `#00515A`, Gold `#C8A96E`

## Projektstruktur

```
UENTDispatcher/
├── Controllers/
│   ├── HomeController.cs           Wheel-Page (Index)
│   ├── DispatcherController.cs     /Dispatcher/Spin, /Confirm, /Status, /Log
│   ├── EmployeesController.cs      Mitarbeitenden-CRUD
│   ├── AccountController.cs        Login/Logout
│   └── HealthController.cs         /Health (Railway-Healthcheck)
├── Models/
│   ├── AppDbContext.cs             DbContext + AppUser/Employee/DispatcherSelection
│   └── DeutscheZeit.cs             Europe/Berlin Zeitzonen-Helper
├── Services/
│   └── DispatcherService.cs        Spin-/Confirm-Logik, 21-Tage-Sperre, Override
├── Views/
│   ├── Account/Login.cshtml
│   ├── Home/Index.cshtml           Wheel-Hauptseite
│   ├── Dispatcher/Log.cshtml       Verlauf als Timeline
│   ├── Employees/Index.cshtml      Mitarbeiterpflege
│   └── Shared/_Layout.cshtml       Sidebar-Layout mit Brand
├── wwwroot/
│   ├── css/dispatcher.css          Bank-CI + spielerische Komponenten
│   ├── js/wheel.js                 Canvas-Wheel-Engine + Page-Logik
│   ├── images/merkur-logo.svg
│   └── favicon.ico
├── Dockerfile
├── railway.toml
└── UENTDispatcher.csproj
```

## Datenmodell

| Entity | Felder | Bedeutung |
|---|---|---|
| `Employee` | `Id`, `Vorname`, `Nachname`, `IstAktiv`, `ErstelltAm` | Personen, die teilnehmen können |
| `DispatcherSelection` | `Id`, `EmployeeId`, `BestaetigtUtc`, `SperrBisUtc`, `BlacklistIgnoriert`, `RestTageUebernommen` | Bestätigte Auswahlen |
| `AppUser` | Klassisch: Login + Rolle | Anmeldung an die App |

## Sperrlogik

- Bestätigte Auswahl → `SperrBisUtc = max(jetzt, alteSperreUtc) + 21 Tage`
- Bei Override (Sperrliste ignoriert): laufende Restsperre wird kumuliert
- Beispiel: Jonas hat noch 7 Tage → Override + Bestätigung → neue Sperre = 7 + 21 = **28 Tage**
- Reine Spins ohne Bestätigung werden **nicht** persistiert und lösen **keine** Sperre aus

## Lokal starten

```bash
cd UENTDispatcher
dotnet run
# → http://localhost:5180 (siehe launchSettings.json)
# Login: admin / Admin1234!
```

Die SQLite-Datei (`uentdispatcher.db`) wird automatisch im ContentRoot angelegt und mit 6 Beispiel-Mitarbeitenden + 2 Standard-Usern befüllt.

## Auf Railway deployen

1. Repo verknüpfen → `andershow88/UENTDispatcher`
2. **Postgres-Plugin** im Railway-Projekt hinzufügen → `DATABASE_URL` wird automatisch gesetzt
3. Deploy starten — Dockerfile wird verwendet
4. Healthcheck: `/Health`

Ohne Postgres-Plugin liegt die DB im Container-Filesystem und ist bei jedem Deploy weg. Alternativ ein Railway-Volume mounten und `DATA_DIR=/data` setzen.

## Standard-Login

| Benutzer | Passwort | Rolle |
|---|---|---|
| `admin` | `Admin1234!` | Admin |
| `user` | `User1234!` | User |

⚠️ Beide Passwörter nach erstem Login ändern (aktuell nicht in V1 enthalten — Pflege via DB oder spätere Erweiterung).

## V1-Scope

✅ Animiertes Wheel mit Spin-Animation (Canvas + CSS-Transition)
✅ Bestätigung mit „Bestätigen" / „Erneut drehen"-Trennung
✅ 21-Tage-Sperre + Anzeige Verfügbar/Gesperrt
✅ Override-Schalter mit kumulativer Sperrzeit
✅ Verlaufs-Timeline mit Tags für Override und Resttag-Übernahme
✅ Mitarbeitenden-CRUD
✅ Modal-Bestätigung mit Konfetti-Animation
✅ Bank-CI (Petrol/Gold) im Look der Merkur Privatbank
✅ Railway-ready (Dockerfile + railway.toml + Postgres-Switch)
