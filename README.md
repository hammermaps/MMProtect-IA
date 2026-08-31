# MMProtect Instanz-Agent

Der **MMProtect Instanz-Agent** ist eine lokale Host-Komponente für den Betrieb von MMProtect-LicenseServer-Instanzen auf genau einem Server. Er stellt eine authentifizierte JSON-REST-API bereit und steuert dort Docker, nginx, lokale Dateien, Backups sowie SQLite- oder externe MySQL-Datenbanken.

> Der Instanz-Agent ist kein Multi-Host-Orchestrator. Die spätere zentrale Verwaltung mehrerer Hosts heißt **Instanzmanager** und kommuniziert mit einzelnen Instanz-Agents über LAN oder WireGuard.

Dieses Repository enthält die Architektur- und Umsetzungsgrundlage für den Instanz-Agent. Die bestehende MMProtect-Runtime – LicenseServer, Encoder und PHP-Loader – wird im Repository [hammermaps/MMProtect](https://github.com/hammermaps/MMProtect) entwickelt.

## Zielarchitektur

```text
Instanzmanager
    |
    | HTTPS / JSON über LAN oder WireGuard
    v
Instanz-Agent (ein lokaler Host)
    |
    +-- Docker Engine
    +-- nginx
    +-- lokales Dateisystem und SQLite
    +-- externe MySQL-Instanzen
    +-- Backup-Speicher
    v
MMProtect-LicenseServer-Container
```

Eine **Instanz** besteht aus einem LicenseServer-Container, einer vollständig übergebenen Domain, einem ausschließlich an `127.0.0.1` gebundenen Host-Port, persistentem Speicher, Datenbankkonfiguration, Secrets/Keys und einem nginx-vHost.

## Abgrenzung zu MMProtect

Der Instanz-Agent ergänzt, statt verändert, die vorhandene Runtime:

| Komponente | Zuständigkeit |
| --- | --- |
| [MMProtect](https://github.com/hammermaps/MMProtect) | LicenseServer-Runtime, Encoder CLI/GUI und PHP Decoder/Loader |
| Instanz-Agent | Lokale Bereitstellung, Betrieb, Backup, Update und Reset von LicenseServer-Containern |
| Instanzmanager | Spätere zentrale Verwaltung mehrerer Instanz-Agents; nicht Bestandteil dieses Projekts |

Der Agent verwendet für neue Instanzen ein konkretes, versioniertes LicenseServer-Image (Tag und Digest), niemals ausschließlich `latest`.

## Geplante API

Die API wird unter `/api/v1` bereitgestellt und über Bearer-Token geschützt. Sie ist für ein internes Verwaltungsnetz vorgesehen und darf nicht ungeschützt im Internet veröffentlicht werden.

| Bereich | Endpunkte |
| --- | --- |
| Host | `GET /host`, `GET /host/health`, `GET /host/capacity` |
| Instanzen | `POST/GET /instances`, `GET/DELETE /instances/{id}` |
| Lifecycle | `POST /instances/{id}/start`, `/stop`, `/restart` |
| Backups | `POST/GET /instances/{id}/backups`, Download und Löschen einzelner Backups |
| Restore | `POST /instances/{id}/restore` |
| Preflight | Docker-, nginx- und MySQL-Prüfungen |
| Updates | Prüfen und Aktualisieren von LicenseServer-Image und nginx-Template |
| Reset | `POST /instances/{id}/reset` mit `runtime` oder explizit bestätigtem `full` |

`Idempotency-Key` verhindert bei Create- und Backup-Requests doppelte Operationen.

## Sicherheitsgrundsätze

- Agent-API nur authentifiziert und bevorzugt über LAN/WireGuard; später auf mTLS und Key-Rotation erweiterbar.
- LicenseServer-Container werden ausschließlich an `127.0.0.1:<hostPort>:8080` veröffentlicht.
- Domains werden als FQDN validiert; sie werden weder aus Namen abgeleitet noch ungeprüft in nginx-Konfigurationen oder Dateinamen eingesetzt.
- API-Keys, KEKs, private Signaturschlüssel und DB-Passwörter werden weder geloggt noch in normalen GET-Antworten ausgegeben. Initiale API-Keys sind nur in der Create-Antwort sichtbar.
- Lokale Secret-Dateien erhalten mindestens die Rechte `0600`; MySQL-Passwörter werden geschützt gespeichert.
- `mysqldump` erhält Zugangsdaten nur per temporärer `0600` Defaults-Datei, nie als Prozessargument.
- Ein Full Reset erfordert die Bestätigung `RESET`, sichert standardmäßig vorher und löscht externe MySQL-Datenbanken nie automatisch.

## Provisionierung

Instanzen werden nicht in einer großen Controller-Methode erzeugt. Der Workflow ist nachvollziehbar in Phasen aufgeteilt:

```text
validate -> reserve -> prepare -> create -> configure -> start -> verify -> commit
                              \-> rollback bei Fehlern
```

Bei MySQL wird vor Create und Start DNS, TCP-Erreichbarkeit, TLS, Authentifizierung, Datenbankauswahl und `SELECT 1` mit Timeouts geprüft. Eine fehlgeschlagene Prüfung bricht die Operation vor dem Containerstart ab.

nginx-Konfigurationen werden atomar geschrieben. Vor jedem Aktivieren oder Reload erfolgt `nginx -t`; ein Fehler stellt den vorherigen Zustand wieder her. nginx-Templates sind versionsgebunden und werden nicht aus dem Internet nachgeladen.

## Backups und Restore

Ein Backup ist ein versioniertes ZIP-Archiv und enthält mindestens:

```text
manifest.json
instance/instance.json
data/
database/
keys/signing-public.pem
```

`manifest.json` beginnt mit `formatVersion: 1`. SQLite wird konsistent per Snapshot gesichert. Backup-Downloads werden gestreamt, nicht vollständig in den Arbeitsspeicher geladen.

Der SQLite-Restore prüft Archivstruktur, Formatversion, Instanz-ID und Provider. Er erzeugt ein Pre-Restore-Backup, stoppt eine laufende Instanz, tauscht das Datenverzeichnis atomar und startet die Instanz wieder. Schlägt der Tausch oder Start fehl, wird das vorherige Datenverzeichnis zurückgespielt. MySQL-Dumps und -Restore folgen in einem späteren Arbeitspaket.

## Geplante Projektstruktur

```text
src/
  InstanceAgent/        ASP.NET-Core-Host und Anwendungslogik
  InstanceAgent.Tests/  Unit- und Integrationstests
docs/
  instance-agent.md     Betrieb, Konfiguration und API
deploy/
  instance-agent/       Service-, nginx- und Konfigurationsbeispiele
```

Laufzeitdaten liegen standardmäßig unter `/var/lib/mmprotect-agent/`; die lokalen nginx-vHosts unter `/etc/nginx/mmprotect.d/`.

## Umsetzungsplan

Die Umsetzung erfolgt in kleinen, testbaren Arbeitspaketen:

1. Projektgerüst, Konfiguration, persistente Agent-ID und lokaler SQLite-Store.
2. Host-, Health- und Capacity-API sowie Authentifizierung und Fehlerformat.
3. Docker-Abstraktion, transaktionssichere Portreservierung und Lifecycle.
4. SQLite- und MySQL-Preflight inklusive geschützter Secret-Speicherung.
5. Domainvalidierung, versionierte nginx-Templates und TLS-Abstraktion.
6. Vollständiger Provisionierungsworkflow mit Idempotenz und Rollback.
7. Backup, Streaming-Download und Restore.
8. Update- und Reset-Workflows mit Backup und Rollback.
9. Unit-/Integrationstests, Betriebsdokumentation und Deployment-Artefakte.

Die detaillierten Anforderungen und die Phasenplanung stehen in [instance-agent-plan.md](instance-agent-plan.md). Die verbindlichen Entwicklungsregeln stehen in [AGENTS.md](AGENTS.md); die Zielstruktur ist in [STRUCTURE.md](STRUCTURE.md) beschrieben.

## Status

Der Instanz-Agent enthält die lokale Host-API, SQLite- und externe MySQL-Provisionierung, Docker-Lifecycle, nginx/TLS-Anbindung, SQLite-Backups und -Restore sowie Update-Erkennung. Die noch offenen Themen sind insbesondere MySQL-Backups/-Restore, transaktionale LicenseServer-/nginx-Updates und produktionsnahe Docker-/nginx-Integrationstests.

## Lizenz

Die Lizenz dieses Repositorys wird vor der ersten Veröffentlichung festgelegt.
