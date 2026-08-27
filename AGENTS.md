# AGENTS.md

## Verbindliche Systembegriffe

Verwende im Code, in Dokumentation und API-Beschreibungen diese Begriffe:

- **Instanzmanager** = zentrale, spätere Multi-Server-Verwaltung.
- **Instanz-Agent** = lokaler Host-Agent auf genau einem Docker/nginx-Server.
- **Instanz** = einzelner MMProtect-LicenseServer-Container.

Die Verbindung Instanzmanager → Instanz-Agent erfolgt über LAN oder WireGuard.

Der Instanz-Agent ist **kein Multi-Host-Orchestrator**. Er führt ausschließlich lokale Operationen aus.

Neue Projektbezeichnung:

```text
src/InstanceAgent/
src/InstanceAgent.Tests/
```


## Zweck

Du arbeitest im Repository `hammermaps/MMProtect`.

Deine Aufgabe ist die Implementierung eines neuen **MMProtect Instanz-Agent**.

Der Instanz-Agent ist eine Host-seitige Orchestrierungskomponente.

Er ist **nicht** die zentrale Verwaltungsinstanz.

Eine spätere zentrale Verwaltungsinstanz wird mehrere Instanz-Agents über eine JSON REST API ansprechen.

Jeder Instanz-Agent verwaltet genau den lokalen Server, auf dem er läuft.

---

## Architekturregel

Halte diese Trennung strikt ein:

```text
Zentrale Verwaltungsinstanz
        |
        | HTTPS / JSON
        v
Instanz-Agent
        |
        +--> Docker Engine
        +--> nginx
        +--> lokales Dateisystem
        +--> SQLite
        +--> externes MySQL
        +--> Backup Storage
        |
        v
MMProtect LicenseServer Container
```

Baue keine Logik für Multi-Host-Orchestrierung in den Instanz-Agent.

Der Agent kennt keine anderen Agents.

---

## Bestehenden LicenseServer nicht unnötig verändern

Der bestehende LicenseServer unter:

```text
src/LicenseServer/
```

ist die Lizenzserver-Runtime.

Neue Host-Orchestrierungslogik gehört in:

```text
src/InstanceAgent/
```

Nur wenn eine kleine Änderung am LicenseServer zwingend für den Agent-Betrieb notwendig ist, darf sie dort vorgenommen werden.

Solche Änderungen müssen:

- minimal sein
- rückwärtskompatibel sein
- getestet sein
- dokumentiert sein

---

## Neue Projekte

Erstelle:

```text
src/InstanceAgent/
src/InstanceAgent.Tests/
```

und füge beide zur bestehenden Solution hinzu.

---

## Technologievorgabe

Bevorzugt:

- C#
- ASP.NET Core
- .NET-Version passend zum bestehenden Repository
- EF Core oder bestehendes Persistenzmuster
- SQLite für lokale Agent-Metadaten
- Docker.DotNet für Docker Engine
- System.IO.Compression für ZIP
- Microsoft.Data.Sqlite für SQLite-Backups

Kein unnötiger externer Service.

---

## REST API

Implementiere mindestens:

```http
GET    /api/v1/host
GET    /api/v1/host/health
GET    /api/v1/host/capacity

POST   /api/v1/instances
GET    /api/v1/instances
GET    /api/v1/instances/{id}
DELETE /api/v1/instances/{id}

POST   /api/v1/instances/{id}/start
POST   /api/v1/instances/{id}/stop
POST   /api/v1/instances/{id}/restart

POST   /api/v1/instances/{id}/backups
GET    /api/v1/instances/{id}/backups
GET    /api/v1/instances/{id}/backups/{backupId}
GET    /api/v1/instances/{id}/backups/{backupId}/download
DELETE /api/v1/instances/{id}/backups/{backupId}
```

Restore:

```http
POST /api/v1/instances/{id}/restore
```

soll mindestens strukturell vorgesehen werden.

---

## Instanz-Erstellung

Die Domain wird vollständig per JSON übergeben.

Nicht aus dem Namen ableiten.

Beispiel:

```json
{
  "name": "kunde-mueller",
  "domain": "license.mueller.example.de",
  "database": {
    "provider": "sqlite"
  }
}
```

MySQL:

```json
{
  "name": "kunde-mueller",
  "domain": "license.mueller.example.de",
  "database": {
    "provider": "mysql",
    "server": "10.10.20.15",
    "port": 3306,
    "user": "mmprotect_mueller",
    "password": "SECRET",
    "database": "mmprotect_mueller",
    "sslMode": "Required"
  }
}
```

Bei MySQL keinen MySQL-Container starten.

Verwende die externe MySQL-Instanz.

Teste die Verbindung vor dem Provisioning.

---

## Docker

Verwende vorzugsweise die Docker Engine API über Docker.DotNet.

Nicht primär `docker` CLI-Kommandos zusammenbauen.

Jeder LicenseServer-Container:

- bekommt einen eindeutigen Namen
- verwendet intern Port 8080
- wird nur an `127.0.0.1:<hostPort>` veröffentlicht
- nutzt `restart unless-stopped` oder äquivalente Docker-Restart-Policy
- erhält persistente Volumes/Bind-Mounts

Niemals MMProtect-Container-Ports auf `0.0.0.0` binden.

---

## nginx

Der Agent verwaltet lokale nginx-vHosts.

Pfad:

```text
/etc/nginx/mmprotect.d/
```

Vor jedem Reload:

```bash
nginx -t
```

Nur bei Erfolg:

```bash
systemctl reload nginx
```

Schreibe nginx-Dateien atomar:

1. temp-Datei schreiben
2. validieren
3. umbenennen
4. nginx testen
5. reload

Bei Fehlern vorherigen Zustand wiederherstellen.

---

## TLS

Version 1:

- Let's Encrypt
- HTTP-01
- Domain kommt vom Request

Nicht von Wildcard-DNS ausgehen.

TLS-Logik als austauschbaren Service kapseln, damit später DNS-01 oder vorhandene Zertifikate ergänzt werden können.

---

## Secrets

Generiere pro Instanz:

- Encoder API Key
- Admin API Key
- KEK
- ECDSA-P256 Signing Key

Private Secrets:

- niemals loggen
- niemals in normalen GET-Antworten zurückgeben
- nicht im Klartext in der Agent-Datenbank speichern
- Dateirechte mindestens 0600, wenn dateibasiert
- DB-Passwörter geschützt speichern

Initiale API-Schlüssel dürfen beim Create-Response einmalig ausgegeben werden.

---

## MySQL-Passwörter

Passwörter niemals als `--password=...` an `mysqldump` übergeben.

Verwende eine temporäre Defaults-Datei:

```ini
[client]
host=...
port=...
user=...
password=...
```

Rechte:

```text
0600
```

Lösche sie garantiert nach Nutzung.

---

## Backups

Ein Backup enthält:

```text
manifest.json
instance/instance.json
data/
database/
keys/signing-public.pem
```

SQLite:

- konsistenten Snapshot erzeugen
- nicht blind laufende DB kopieren

MySQL:

- `mysqldump --single-transaction`
- optional routines/triggers/events
- Credentials nicht in Prozessliste

ZIP-Dateien dürfen für Download nicht komplett in RAM geladen werden.

Streaming verwenden.

---

## Backup-Manifeste

Jedes Backup benötigt:

```json
{
  "formatVersion": 1
}
```

Keine unversionierten Backupformate erzeugen.

Restore-Kompatibilität muss bei Änderungen berücksichtigt werden.

---

## Port Allocation

Portbereich konfigurierbar machen.

Beispiel:

```text
18000-18999
```

Die Vergabe muss thread-/prozesssicher sein.

Parallel eintreffende Provisioning-Requests dürfen niemals denselben Port erhalten.

---

## Provisionierung als Workflow behandeln

Implementiere Instanzerstellung nicht als lange unstrukturierte Controller-Methode.

Erzeuge eine Service-/Workflow-Abstraktion.

Phasen:

```text
validate
reserve
prepare
create
configure
start
verify
commit
```

Fehler:

```text
rollback
```

Rollback muss best-effort und nachvollziehbar sein.

---

## Idempotenz

Unterstütze:

```http
Idempotency-Key
```

Mindestens bei:

```http
POST /api/v1/instances
POST /api/v1/instances/{id}/backups
```

Wiederholte Requests dürfen keine Duplikate erzeugen.

---

## Fehlerformat

Maschinenlesbare Fehler.

Beispiel:

```json
{
  "error": "NGINX_CONFIG_INVALID",
  "message": "nginx configuration validation failed",
  "traceId": "..."
}
```

Definiere stabile Fehlercodes.

Keine Secrets in `message`, Exceptions oder Logs.

---

## Logging

Structured Logging.

Logge:

- instanceAgentId
- instanceId
- backupId
- operation
- result
- duration

Nicht loggen:

- API Keys
- KEK
- private keys
- DB passwords
- Connection Strings mit Passwort

---

## Datenmodell

Mindestens:

```text
instances
backups
ports
events
idempotency_records
```

Instanz-Agent-ID persistent speichern.

---

## Tests

Erstelle Unit- und Integrationstests.

Pflichttests:

### Domain

- gültiger FQDN
- ungültige Domain
- doppelte Domain

### Ports

- freie Ports
- Portbereich voll
- Parallelzugriff

### Docker

- Create
- Start
- Stop
- Restart
- Delete
- Fehler/Timeout

### MySQL

- gültige Verbindung
- falscher Host
- falsche Credentials
- nicht vorhandene DB

### nginx

- korrekte Konfiguration
- `nginx -t` Fehler
- Rollback

### Backup

- SQLite Snapshot
- MySQL Dump
- ZIP-Inhalt
- Manifest
- Download Streaming

### Security

- Secrets nicht in GET
- Secrets nicht in Logs
- Auth erforderlich

---

## Keine unnötigen Abhängigkeiten

Bevor du ein neues NuGet-Paket hinzufügst:

1. prüfen, ob .NET/BCL reicht
2. prüfen, ob Repository bereits passende Library nutzt
3. nur dann neue Dependency ergänzen

---

## Codequalität

Bevorzugen:

- kleine Services
- klare Interfaces
- async I/O
- CancellationToken
- typed options
- typed results/DTOs
- Dependency Injection
- keine statischen globalen Zustände
- keine Shell-String-Konkatenation mit unvalidierten Benutzerdaten

---

## Shell Commands

Wenn externe Programme nötig sind, z. B.:

```text
nginx
systemctl
mysqldump
certbot
```

verwende einen zentralen `ProcessRunner`.

Keine Shell mit:

```text
/bin/sh -c "<user input>"
```

Parameter als getrennte Argumente übergeben.

Timeouts setzen.

stdout/stderr begrenzen und sicher loggen.

---

## Domainvalidierung

Die Domain aus dem Request darf nie direkt ungeprüft in nginx-Konfiguration oder Dateinamen gelangen.

Validiere mindestens:

- FQDN-Syntax
- keine Leerzeichen
- keine `/`
- keine `;`
- keine Zeilenumbrüche
- keine nginx-Direktiven
- keine IP-Adresse, sofern nicht explizit erlaubt
- Länge
- IDN bei Bedarf bewusst normalisieren

Für Dateinamen immer `instanceId` verwenden, nicht die Domain.

---

## Arbeitsreihenfolge

Arbeite bevorzugt in dieser Reihenfolge:

1. Repository analysieren
2. bestehende Patterns übernehmen
3. InstanceAgent Projekt anlegen
4. Host API
5. lokale Persistenz
6. Auth
7. Docker-Abstraktion
8. Ports
9. SQLite Provisioning
10. MySQL Provisioning
11. nginx
12. TLS
13. Gesamt-Provisionierungsworkflow
14. Backups
15. Download
16. Restore
17. Härtung
18. Dokumentation
19. Tests

---

## Änderungen klein halten

Implementiere in logisch getrennten Commits bzw. Arbeitspaketen.

Beispiel:

```text
feat(agent): add Instanz-Agent project
feat(agent): add host inventory API
feat(agent): add docker instance lifecycle
feat(agent): add nginx provisioning
feat(agent): add mysql instance config
feat(agent): add backups
test(agent): add provisioning integration tests
```

---

## Dokumentation

Pflege mindestens:

```text
docs/instance-agent.md
```

Dort dokumentieren:

- Installation
- Konfiguration
- Auth
- Instanz-Agent-ID
- Portbereich
- Docker Socket
- nginx Integration
- TLS
- API
- Backup
- Restore
- Security
- Troubleshooting

---

## Definition of Done

Nicht als fertig markieren, bevor folgende Punkte erfüllt sind:

- `dotnet build` erfolgreich
- vorhandene Tests weiterhin erfolgreich
- neue Agent-Tests erfolgreich
- Agent startet ohne Docker/nginx mit verständlichem degraded status
- Auth funktioniert
- SQLite-Instanz kann erstellt werden
- MySQL-Instanz kann mit externen Credentials erstellt werden
- Container nur localhost-bound
- nginx-Konfiguration funktioniert
- Backup ZIP funktioniert
- Download funktioniert
- keine bekannten Secret-Leaks
- Rollback bei Provisionierungsfehler getestet
- API dokumentiert


## Preflight und Updates

Der Instanz-Agent muss externe Abhängigkeiten aktiv prüfen.

### MySQL

Bei `create` und `start` einer MySQL-basierten Instanz:

- DNS/Host prüfen
- TCP Port prüfen
- TLS gemäß Konfiguration prüfen
- Authentifizierung prüfen
- Datenbank auswählen
- `SELECT 1` ausführen
- Timeout verwenden
- differenzierte Fehlercodes liefern

Eine fehlgeschlagene Prüfung muss Create/Start abbrechen.

### nginx

Vor jedem Apply oder Reload:

```bash
nginx -t
```

nginx-Templates müssen versioniert sein.

Pro Instanz persistieren:

```text
installedTemplateVersion
configRevision
```

Neue Templates werden mit einer neuen Instanz-Agent-Version ausgeliefert. Nicht eigenständig unversionierte Templates aus dem Internet laden.

Implementiere Update-Erkennung und sichere Migration alter nginx-Konfigurationen.

### LicenseServer Updates

Implementiere:

```http
GET  /api/v1/instances/{id}/updates
POST /api/v1/instances/{id}/updates/check
POST /api/v1/instances/{id}/updates/license-server
POST /api/v1/instances/{id}/updates/nginx
POST /api/v1/instances/{id}/updates/all
```

Vor einem LicenseServer-Update standardmäßig Backup erstellen.

Update muss rollbackfähig sein.

Persistente Daten dürfen beim Container-Update nicht gelöscht werden.

Speichere konkrete Tags/Digests, nicht nur `latest`.

### Vollständiger Instanz-Reset

Implementiere:

```http
POST /api/v1/instances/{id}/reset
```

Mindestens Modi:

```text
runtime
full
```

`runtime` behält persistente Daten und baut Runtime/nginx neu auf.

`full` entfernt:

- Container
- nginx-Konfiguration
- Portreservierung
- lokale persistente Instanzdaten
- lokale SQLite DB
- lokale Secrets
- Signing Keys
- lokale Instanz-Metadaten

Standardwerte:

```text
createBackup=true
deleteBackups=false
```

Externe MySQL-Datenbanken dürfen bei `full` **nicht automatisch gelöscht** werden.

Fordere für Full Reset eine explizite Bestätigung, z. B.:

```json
{
  "mode": "full",
  "confirmation": "RESET"
}
```

Keinen serverweiten Agent-Reset in Version 1 implementieren.

