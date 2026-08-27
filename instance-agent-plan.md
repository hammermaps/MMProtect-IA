# MMProtect – Instanzmanager / Instanz-Agent Architektur

## 1. Verbindliche Begriffe

Für die weitere Entwicklung gelten diese Begriffe:

- **Instanzmanager**: zentrale Verwaltungsinstanz. Verwaltet mehrere Server bzw. Instanz-Agents.
- **Instanz-Agent**: lokaler Agent auf genau einem Server. Steuert dort Docker, nginx, Datenhaltung, Backups und Restore.
- **Instanz**: ein einzelner MMProtect-LicenseServer-Container samt zugehöriger Domain, Daten und Datenbankkonfiguration.

Die Kommunikationsstrecke zwischen Instanzmanager und Instanz-Agent erfolgt ausschließlich über ein internes Verwaltungsnetz, bevorzugt:

- LAN oder
- WireGuard

Die Agent-API soll nicht direkt öffentlich aus dem Internet erreichbar sein.

## 2. Zielarchitektur

```text
                         +---------------------------+
                         |      Instanzmanager       |
                         |   Zentrale Verwaltung     |
                         +-------------+-------------+
                                       |
                              LAN oder WireGuard
                                       |
          +----------------------------+----------------------------+
          |                            |                            |
          v                            v                            v

+----------------------+   +----------------------+   +----------------------+
| Server 1             |   | Server 2             |   | Server 3             |
|                      |   |                      |   |                      |
| Instanz-Agent        |   | Instanz-Agent        |   | Instanz-Agent        |
| Docker               |   | Docker               |   | Docker               |
| nginx                |   | nginx                |   | nginx                |
|                      |   |                      |   |                      |
| MMProtect Instanzen  |   | MMProtect Instanzen  |   | MMProtect Instanzen  |
+----------------------+   +----------------------+   +----------------------+
```

Der **Instanzmanager entscheidet, was auf welchem Server geschehen soll**.

Der **Instanz-Agent führt die angeforderte Operation ausschließlich auf seinem lokalen Server aus**.

Docker und nginx bleiben lokale Ausführungskomponenten des jeweiligen Servers.

Die eigentliche Lizenzlogik verbleibt im bestehenden `LicenseServer`.

## 3. Verantwortungsgrenzen

### Instanzmanager

Noch nicht Bestandteil dieses Implementierungspakets.

Spätere Aufgaben:

- mehrere Instanz-Agents registrieren und verwalten
- Verbindungsstatus der Server überwachen
- Zielserver für neue Instanzen auswählen
- Instanzen serverübergreifend auflisten
- zentrale Benutzer-/Mandantenverwaltung
- globale Policies
- zentrale Backup-Übersicht
- Kapazitätsplanung
- ggf. Scheduling/Placement
- Kommunikation mit Agents über LAN oder WireGuard

### Instanz-Agent

Bestandteil dieses Projekts.

Aufgaben pro lokalem Server:

- authentifizierte JSON REST API
- Docker Engine steuern
- nginx verwalten
- Ports reservieren
- Domains konfigurieren
- TLS konfigurieren
- SQLite- oder externe MySQL-Verbindungen einrichten
- MMProtect-Instanzen erstellen/starten/stoppen/neustarten/löschen
- Backups erzeugen
- Backup-ZIP zum Download bereitstellen
- Restore durchführen
- Host-, Agent- und Instanzstatus melden

### Instanz

Eine Instanz besteht aus:

- genau einem MMProtect-LicenseServer-Container
- genau einer öffentlich verwendeten Domain
- lokalem Host-Port
- persistentem Data-Verzeichnis
- SQLite oder externer MySQL-Datenbank
- Secrets/Keys
- nginx-vHost
- optionalem TLS-Zertifikat
- Backup-Historie



## 4. Management-Netz

Die Kommunikation zwischen Instanzmanager und Instanz-Agent soll über ein nicht öffentliches Verwaltungsnetz erfolgen.

Beispiel WireGuard:

```text
10.100.0.1     Instanzmanager
10.100.0.11    Instanz-Agent Server 1
10.100.0.12    Instanz-Agent Server 2
10.100.0.13    Instanz-Agent Server 3
```

Beispiel Agent-Endpunkte:

```text
https://10.100.0.11:8443
https://10.100.0.12:8443
https://10.100.0.13:8443
```

Der Instanzmanager speichert je Server mindestens:

```json
{
  "id": "server-01",
  "name": "license-node-01",
  "agentUrl": "https://10.100.0.11:8443",
  "connection": "wireguard",
  "status": "online"
}
```

Der Instanz-Agent kennt keine anderen Instanz-Agents und enthält keinerlei Multi-Host-Placement-Logik.

 Kommunikationsmodell

Die spätere Verwaltungsinstanz kommuniziert per HTTPS mit jedem Instanz-Agent.

Beispiel:

```text
Management
   |
   +--> https://agent01.example.net/api/v1/...
   |
   +--> https://agent02.example.net/api/v1/...
   |
   +--> https://agent03.example.net/api/v1/...
```

Der Agent muss daher keine Informationen über andere Hosts kennen.

Er ist ein lokaler Executor und Inventory Provider.

---

## 5. REST API

### 5.1 Host-Informationen

```http
GET /api/v1/host
GET /api/v1/host/health
GET /api/v1/host/capacity
```

Beispiel:

```json
{
  "instanceAgentId": "srv-01",
  "hostname": "docker01.example.net",
  "version": "0.1.0",
  "docker": {
    "available": true,
    "version": "..."
  },
  "nginx": {
    "available": true,
    "version": "..."
  },
  "instances": 12
}
```

`/capacity` kann später u. a. liefern:

```json
{
  "cpu": {
    "logicalProcessors": 24
  },
  "memory": {
    "totalBytes": 68719476736,
    "availableBytes": 42949672960
  },
  "disk": {
    "totalBytes": 1099511627776,
    "freeBytes": 824633720832
  },
  "ports": {
    "rangeStart": 18000,
    "rangeEnd": 18999,
    "used": 12
  }
}
```

---

## 6. Instanz-API

### Endpunkte

```http
POST   /api/v1/instances
GET    /api/v1/instances
GET    /api/v1/instances/{id}
DELETE /api/v1/instances/{id}

POST   /api/v1/instances/{id}/start
POST   /api/v1/instances/{id}/stop
POST   /api/v1/instances/{id}/restart
```

---

## 7. Instanz erstellen

Die vollständige Domain wird von der Verwaltungsinstanz übergeben.

### SQLite

```json
{
  "name": "kunde-mueller",
  "domain": "license.mueller.example.de",

  "database": {
    "provider": "sqlite"
  },

  "leaseTtlMinutes": 1440,
  "gracePeriodDays": 7
}
```

### Externes MySQL

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
  },

  "leaseTtlMinutes": 1440,
  "gracePeriodDays": 7
}
```

Der Agent startet bei `provider=mysql` **keinen MySQL-Container**.

Er verwendet die angegebenen externen Zugangsdaten.

Vor der Instanzerstellung muss die Verbindung geprüft werden.

---

## 8. Secrets

Beim Anlegen erzeugt der Instanz-Agent:

- Encoder API Key
- Admin API Key
- KEK
- ECDSA-P256 Signing Key

Die generierten API-Schlüssel dürfen nur bei Erstellung einmalig ausgegeben werden.

Beispiel:

```json
{
  "id": "01K...",
  "status": "running",
  "domain": "license.mueller.example.de",
  "url": "https://license.mueller.example.de",
  "credentials": {
    "adminApiKey": "...",
    "encoderApiKey": "..."
  }
}
```

Bei späterem GET:

```json
{
  "credentials": {
    "adminApiKeyConfigured": true,
    "encoderApiKeyConfigured": true
  }
}
```

Private Secrets dürfen nicht über normale GET-Endpunkte ausgelesen werden.

---

## 9. Portverwaltung

Jede Instanz erhält einen lokalen Host-Port.

Beispielbereich:

```text
18000-18999
```

Container-Binding:

```text
127.0.0.1:18023:8080
```

Nicht erlaubt:

```text
0.0.0.0:18023:8080
```

Der LicenseServer darf ausschließlich über nginx öffentlich erreichbar sein.

Die Portvergabe muss transaktionssicher erfolgen, damit parallele Requests keinen Port doppelt vergeben.

---

## 10. Docker

Der Instanz-Agent soll die Docker Engine direkt über die API ansprechen.

Empfohlen:

```text
Docker.DotNet
```

Unix Socket:

```text
/var/run/docker.sock
```

Beispielparameter für eine SQLite-Instanz:

```text
ASPNETCORE_HTTP_PORTS=8080
DatabaseProvider=sqlite
ConnectionStrings__Sqlite=Data Source=/data/mm_license.db
Security__EncoderApiKeys__0=<generated>
Security__AdminApiKeys__0=<generated>
Security__KeyEncryptionKey=<generated>
Security__SigningPrivateKeyFile=/run/secrets/signing-private.pem
ReverseProxy__Enabled=true
ReverseProxy__ForwardLimit=1
```

Volumes:

```text
/var/lib/mmprotect/instances/{id}/data:/data
/var/lib/mmprotect/instances/{id}/keys/signing-private.pem:/run/secrets/signing-private.pem:ro
```

---

## 11. nginx

Der Agent verwaltet nginx nur auf seinem lokalen Host.

Empfohlener Pfad:

```text
/etc/nginx/mmprotect.d/
```

Pro Instanz:

```text
/etc/nginx/mmprotect.d/{instance-id}.conf
```

Die Domain wird exakt aus dem JSON übernommen.

Beispiel:

```nginx
server {
    listen 80;
    server_name license.mueller.example.de;

    location /.well-known/acme-challenge/ {
        root /var/www/letsencrypt;
    }

    location / {
        return 301 https://$host$request_uri;
    }
}

server {
    listen 443 ssl http2;
    server_name license.mueller.example.de;

    ssl_certificate     /etc/letsencrypt/live/license.mueller.example.de/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/license.mueller.example.de/privkey.pem;

    client_max_body_size 1m;

    location / {
        proxy_pass http://127.0.0.1:18023;

        proxy_http_version 1.1;

        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
    }
}
```

Nach jeder Änderung:

```bash
nginx -t
```

Nur bei Erfolg:

```bash
systemctl reload nginx
```

Fehlschläge müssen Rollback-fähig sein.

---

## 12. TLS

Da die Domain vollständig vom Management übergeben wird, darf nicht von einer gemeinsamen Zone oder Wildcard-Domain ausgegangen werden.

TLS-Modi:

```json
{
  "tls": {
    "mode": "letsencrypt",
    "email": "admin@example.de"
  }
}
```

Optional später:

```json
{
  "tls": {
    "mode": "existing",
    "certificate": "...",
    "privateKey": "..."
  }
}
```

Für Version 1 sollte Let's Encrypt HTTP-01 unterstützt werden.

DNS-01 kann später ergänzt werden.

---

## 13. Backup

### API

```http
POST   /api/v1/instances/{id}/backups
GET    /api/v1/instances/{id}/backups
GET    /api/v1/instances/{id}/backups/{backupId}
GET    /api/v1/instances/{id}/backups/{backupId}/download
DELETE /api/v1/instances/{id}/backups/{backupId}
```

Backups werden pro Instanz erzeugt.

Pfad:

```text
/var/lib/mmprotect/backups/{instance-id}/
```

---

## 14. Backup ZIP Format

```text
mmprotect-<instance>-YYYY-MM-DD_HHmmss.zip
│
├── manifest.json
│
├── instance/
│   └── instance.json
│
├── data/
│   └── ...
│
├── database/
│   ├── mm_license.db
│   └── ODER
│       mm_license.sql
│
└── keys/
    └── signing-public.pem
```

Beispiel `manifest.json`:

```json
{
  "formatVersion": 1,
  "instanceAgentId": "srv-01",
  "instance": {
    "id": "abc123",
    "name": "kunde-mueller",
    "domain": "license.mueller.example.de"
  },
  "database": {
    "provider": "sqlite"
  },
  "createdAt": "2026-08-27T08:15:22+02:00"
}
```

---

## 15. SQLite Backup

Nicht einfach die laufende DB-Datei kopieren.

Es muss ein konsistenter Snapshot erzeugt werden.

Bevorzugt über SQLite Backup API oder:

```bash
sqlite3 /data/mm_license.db ".backup '/tmp/mm_license.db'"
```

Danach wird der Snapshot in das ZIP geschrieben.

---

## 16. MySQL Backup

Bei externem MySQL:

```bash
mysqldump \
  --single-transaction \
  --routines \
  --triggers \
  --events \
  --defaults-extra-file=/tmp/mmprotect-xxxxx.cnf \
  <database>
```

Temporäre Credentials-Datei:

```ini
[client]
host=10.10.20.15
port=3306
user=mmprotect_mueller
password=SECRET
```

Rechte:

```text
0600
```

Die Datei muss in einem `finally`-Block bzw. garantiert nach dem Dump gelöscht werden.

Passwörter niemals als CLI-Argument übergeben.

---

## 17. Download

Download:

```http
GET /api/v1/instances/{id}/backups/{backupId}/download
```

Response:

```http
Content-Type: application/zip
Content-Disposition: attachment; filename="mmprotect-kunde-mueller-2026-08-27_081522.zip"
```

Große Dateien sollen gestreamt werden.

Nicht vollständig in den RAM laden.

---

## 18. Restore

Von Anfang an mit vorsehen:

```http
POST /api/v1/instances/{id}/restore
```

Ablauf:

```text
Upload ZIP
   ↓
manifest validieren
   ↓
Instanz stoppen
   ↓
automatisches Pre-Restore-Backup
   ↓
Data wiederherstellen
   ↓
SQLite ersetzen oder MySQL importieren
   ↓
Instanz starten
   ↓
Health Check
```

Das Backupformat muss deshalb versioniert sein.

---

## 19. Datenmodell des Agents

### instances

```text
id
name
domain
container_id
container_name
host_port
database_provider
database_server
database_port
database_user
database_name
database_ssl_mode
status
created_at
updated_at
```

Das MySQL-Passwort darf nicht im Klartext in dieser Tabelle gespeichert werden.

Secrets benötigen einen geschützten Secret Store oder verschlüsselte lokale Speicherung.

### backups

```text
id
instance_id
file_name
file_path
file_size
status
database_provider
created_at
completed_at
error
```

### ports

```text
port
instance_id
reserved_at
```

### events

```text
id
instance_id
event_type
message
created_at
```

---

## 20. Lokale Verzeichnisstruktur

```text
/var/lib/mmprotect-agent/
├── agent.db
├── instances/
│   └── {instance-id}/
│       ├── instance.json
│       ├── data/
│       ├── keys/
│       │   ├── signing-private.pem
│       │   └── signing-public.pem
│       └── secrets/
│
├── backups/
│   └── {instance-id}/
│
└── tmp/
```

nginx separat:

```text
/etc/nginx/mmprotect.d/
```

---

## 21. Projektstruktur

```text
src/InstanceAgent/
├── Api/
│   ├── HostEndpoints.cs
│   ├── InstanceEndpoints.cs
│   └── BackupEndpoints.cs
│
├── Application/
│   ├── Instances/
│   ├── Backups/
│   └── Host/
│
├── Domain/
│   ├── Instances/
│   ├── Backups/
│   └── Ports/
│
├── Infrastructure/
│   ├── Docker/
│   │   ├── DockerService.cs
│   │   └── DockerOptions.cs
│   │
│   ├── Nginx/
│   │   ├── NginxService.cs
│   │   ├── NginxTemplateRenderer.cs
│   │   └── NginxOptions.cs
│   │
│   ├── Persistence/
│   │   ├── AgentDbContext.cs
│   │   └── Migrations/
│   │
│   ├── Backup/
│   │   ├── BackupService.cs
│   │   ├── SqliteBackupProvider.cs
│   │   ├── MySqlBackupProvider.cs
│   │   └── ZipBackupWriter.cs
│   │
│   ├── Security/
│   │   ├── SecretStore.cs
│   │   └── KeyGenerator.cs
│   │
│   └── System/
│       ├── ProcessRunner.cs
│       └── PortAllocator.cs
│
├── Models/
│   ├── Requests/
│   └── Responses/
│
├── Program.cs
├── appsettings.json
└── InstanceAgent.csproj
```

Tests:

```text
src/InstanceAgent.Tests/
├── Unit/
├── Integration/
└── Fixtures/
```

---

## 22. Authentifizierung Management → Agent

Der Agent darf nicht ungeschützt erreichbar sein.

Für Version 1:

```http
Authorization: Bearer <AGENT_API_KEY>
```

Besser vorbereiten auf:

- mehrere Management API Keys
- Key Rotation
- mTLS
- Agent Enrollment

Langfristig empfohlen:

```text
HTTPS + mTLS + API Token
```

---

## 23. Idempotenz

Managementsysteme wiederholen Requests bei Netzwerkfehlern.

Darum sollte `POST /instances` einen optionalen Idempotency-Key akzeptieren:

```http
Idempotency-Key: 1f38c3...
```

Doppeltes Provisioning muss verhindert werden.

---

## 24. Fehlerbehandlung

Fehlercodes sollen maschinenlesbar sein.

Beispiele:

```text
DOMAIN_INVALID
DOMAIN_ALREADY_ASSIGNED
PORT_EXHAUSTED
DOCKER_UNAVAILABLE
DOCKER_CREATE_FAILED
MYSQL_CONNECTION_FAILED
NGINX_CONFIG_INVALID
TLS_ISSUE_FAILED
INSTANCE_HEALTHCHECK_FAILED
BACKUP_FAILED
RESTORE_FAILED
```

Format:

```json
{
  "error": "MYSQL_CONNECTION_FAILED",
  "message": "Connection to MySQL failed.",
  "traceId": "..."
}
```

Keine Passwörter oder Secrets in Fehlermeldungen.

---

## 25. Provisionierungs-Transaktion

Erstellung:

```text
Request validieren
   ↓
Idempotency prüfen
   ↓
Domain reservieren
   ↓
Port reservieren
   ↓
DB-Konfiguration prüfen
   ↓
Secrets erzeugen
   ↓
Dateisystem vorbereiten
   ↓
Docker Container erstellen
   ↓
nginx Config schreiben
   ↓
TLS erzeugen
   ↓
nginx -t
   ↓
nginx reload
   ↓
Container starten
   ↓
Health Check
   ↓
Status RUNNING
```

Bei Fehlern:

```text
Rollback
- nginx Config entfernen
- Container entfernen
- Port freigeben
- Domain freigeben
- temporäre Dateien löschen
```

Persistente Daten nur dann löschen, wenn sicher festgestellt wurde, dass die Instanz noch nicht erfolgreich provisioniert wurde.

---

## 26. Löschen einer Instanz

`DELETE /instances/{id}` darf standardmäßig nicht sofort alle Daten vernichten.

Empfehlung:

```http
DELETE /api/v1/instances/{id}?deleteData=false
```

Standard:

```text
Container entfernen
nginx Config entfernen
Port freigeben
Metadaten deaktivieren
Data behalten
```

Explizite Löschung:

```http
DELETE /api/v1/instances/{id}?deleteData=true
```

Vor vollständiger Löschung optional automatisches finales Backup.

---

## 27. Host-ID

Jeder Agent benötigt eine persistente eindeutige ID.

Beispiel:

```text
srv-01-01J...
```

Sie darf sich nach Neustart nicht ändern.

Die spätere Verwaltungsinstanz identifiziert darüber den Host.

---

## 28. Agent-Version

Jede API-Antwort für Hostinformationen sollte enthalten:

```json
{
  "agentVersion": "0.1.0",
  "apiVersion": "v1"
}
```

Damit kann die spätere Managementplattform kompatible Hosts erkennen.

---

## 29. Priorisierte Umsetzung

### Phase 1 – Grundgerüst

1. `InstanceAgent` Projekt erstellen
2. zur Solution hinzufügen
3. Konfiguration
4. Instanz-Agent-ID
5. Agent-Datenbank
6. Auth Middleware
7. `/health`
8. `/host`

### Phase 2 – Docker

1. Docker Client
2. Container-Liste
3. Portverwaltung
4. Container erstellen
5. Start/Stop/Restart/Delete
6. Status synchronisieren

### Phase 3 – Datenbanken

1. SQLite
2. MySQL-Verbindungsmodell
3. MySQL Connection Test
4. Secret-Speicherung

### Phase 4 – nginx/TLS

1. Domainvalidierung
2. nginx Templates
3. Config-Dateien
4. `nginx -t`
5. Reload
6. TLS

### Phase 5 – vollständiges Provisioning

1. Create Workflow
2. Rollback
3. Health Check
4. Idempotenz
5. API Response

### Phase 6 – Backup

1. Backup-Metadaten
2. Data Backup
3. SQLite Snapshot
4. MySQL Dump
5. ZIP
6. Download
7. Delete

### Phase 7 – Restore

1. ZIP Upload
2. Manifest Validation
3. Pre-Restore Backup
4. SQLite Restore
5. MySQL Restore
6. Data Restore
7. Health Check
8. Rollback soweit möglich

### Phase 8 – Tests/Härtung

1. Unit Tests
2. Docker Integration Tests
3. SQLite Integration
4. MySQL Integration
5. nginx Integration
6. Security Tests
7. Concurrency/Port Tests
8. Backup-/Restore-Roundtrip

---

## 30. Definition of Done

Das Feature gilt als umgesetzt, wenn:

- Agent separat vom LicenseServer läuft
- Agent exakt einen Host verwaltet
- Management API authentifiziert ist
- Domain per JSON angenommen wird
- SQLite-Instanzen erstellt werden können
- externe MySQL-Zugangsdaten beim Erstellen angegeben werden können
- MySQL-Verbindung vor Provisioning geprüft wird
- MMProtect-Container nur an `127.0.0.1` gebunden sind
- nginx automatisch konfiguriert wird
- `nginx -t` vor Reload ausgeführt wird
- TLS funktioniert
- Start/Stop/Restart/Delete funktionieren
- Backups Data + DB enthalten
- SQLite konsistent gesichert wird
- MySQL per Dump gesichert wird
- ZIP-Download funktioniert
- Restore vorgesehen bzw. implementiert ist
- keine Secrets in Logs/API-GET-Antworten erscheinen
- parallele Provisionierung keine Portkollision erzeugt
- automatisierte Tests vorhanden sind


# Erweiterung: Preflight-, Validierungs-, Update- und Reset-Funktionen

## A. Grundsatz

Der Instanz-Agent muss vor zustandsverändernden Operationen die lokalen Voraussetzungen prüfen.

Insbesondere gilt:

- `create`: Abhängigkeiten vor Provisionierung prüfen
- `start`: Datenbank und lokale Konfiguration vor Containerstart prüfen
- `restart`: wie `start`
- `update`: neue Runtime-/Template-Version zuerst prüfen und validieren
- `nginx apply/update`: immer Syntaxprüfung vor Reload
- `reset`: destruktive Aktion nur mit expliziter Bestätigung und eindeutigem Scope

---

## B. Preflight-Endpunkte

```http
GET  /api/v1/preflight
GET  /api/v1/preflight/docker
GET  /api/v1/preflight/nginx

POST /api/v1/preflight/mysql
POST /api/v1/instances/{id}/preflight
```

### MySQL-Preflight

```json
{
  "server": "10.10.20.15",
  "port": 3306,
  "user": "mmprotect_mueller",
  "password": "SECRET",
  "database": "mmprotect_mueller",
  "sslMode": "Required"
}
```

Response:

```json
{
  "reachable": true,
  "authenticated": true,
  "databaseExists": true,
  "serverVersion": "8.4.x",
  "latencyMs": 12
}
```

Fehlercodes:

```text
MYSQL_DNS_FAILED
MYSQL_HOST_UNREACHABLE
MYSQL_PORT_UNREACHABLE
MYSQL_TLS_FAILED
MYSQL_AUTH_FAILED
MYSQL_DATABASE_NOT_FOUND
MYSQL_QUERY_FAILED
```

Keine Secrets in Logs oder Responses.

---

## C. MySQL-Prüfung bei Create

Bei `POST /api/v1/instances` und `database.provider=mysql`:

```text
JSON validieren
   ↓
DNS/Adresse auflösen
   ↓
TCP Server:Port prüfen
   ↓
TLS gemäß sslMode
   ↓
Authentifizierung
   ↓
Datenbank auswählen
   ↓
SELECT 1
   ↓
erst danach Provisionierung
```

Eine nicht erreichbare oder nicht verwendbare MySQL-Datenbank verhindert die Instanzerstellung.

---

## D. MySQL-Prüfung bei Start

Bei:

```http
POST /api/v1/instances/{id}/start
```

muss bei MySQL-Instanzen vor Containerstart erneut geprüft werden:

```text
Instanz laden
   ↓
provider=mysql?
   ├── nein → fortfahren
   └── ja → MySQL Preflight
                 ↓
             erfolgreich?
              ├── ja → Container starten
              └── nein → Start abbrechen
```

Fehler:

```json
{
  "error": "MYSQL_UNAVAILABLE",
  "message": "Configured MySQL database is currently unavailable.",
  "traceId": "..."
}
```

---

## E. nginx-Prüfung

Jede Änderung an nginx:

```text
Template rendern
   ↓
temporäre Config schreiben
   ↓
semantische Agent-Prüfung
   ↓
nginx -t
   ↓
nur bei Erfolg aktivieren
   ↓
nginx reload
   ↓
Healthcheck
```

Mindestens prüfen:

1. nginx installiert
2. nginx-Version
3. `nginx -t`
4. `server_name`
5. `proxy_pass` auf `127.0.0.1:<port>`
6. Zertifikatspfade
7. Include-Pfad
8. doppelte Agent-Domains
9. Template-Kompatibilität

---

## F. Versionierte nginx-Vorlagen

Jede Vorlage hat eine explizite Version:

```text
templateVersion: 3
```

Pro Instanz speichern:

```json
{
  "nginx": {
    "templateVersion": 2,
    "configRevision": 7
  }
}
```

Der Agent kennt:

```text
CurrentNginxTemplateVersion = 3
```

Damit:

```text
Installiert v2
Aktuell v3
   ↓
Update verfügbar
```

Wichtig: Der Agent lädt nicht eigenständig beliebige Templates aus dem Internet.

Neue nginx-Vorlagen werden mit einer neuen Instanz-Agent-Version ausgeliefert. So bleiben Code und Templates reproduzierbar gekoppelt.

---

## G. nginx-Endpunkte

```http
GET  /api/v1/nginx
GET  /api/v1/nginx/templates
GET  /api/v1/instances/{id}/nginx
POST /api/v1/instances/{id}/nginx/validate
POST /api/v1/instances/{id}/nginx/update
POST /api/v1/nginx/update-all
```

Beispiel Status:

```json
{
  "instanceId": "abc123",
  "domain": "license.mueller.example.de",
  "installedTemplateVersion": 2,
  "currentTemplateVersion": 3,
  "updateAvailable": true,
  "syntaxValid": true
}
```

Update-Workflow:

```text
neues Template rendern
   ↓
temp Config
   ↓
nginx -t
   ↓
alte Config sichern
   ↓
atomar ersetzen
   ↓
nginx -t
   ↓
reload
   ↓
Healthcheck
   ↓
Template-Version committen
```

Fehler → Rollback auf alte Config.

---

## H. LicenseServer-Container Updates

Endpunkte:

```http
GET  /api/v1/instances/{id}/updates
POST /api/v1/instances/{id}/updates/check
POST /api/v1/instances/{id}/updates/license-server
POST /api/v1/instances/{id}/updates/nginx
POST /api/v1/instances/{id}/updates/all
```

Optional hostweit:

```http
GET  /api/v1/updates
POST /api/v1/updates/check
POST /api/v1/updates/license-server
POST /api/v1/updates/nginx
```

---

## I. Container-Versionierung

Nicht ausschließlich `latest` verwenden.

Pro Instanz speichern:

```json
{
  "image": {
    "repository": "mmprotect-license-server",
    "tag": "0.4.2",
    "digest": "sha256:...",
    "imageId": "sha256:..."
  }
}
```

Update-Policy:

```json
{
  "updatePolicy": {
    "channel": "stable",
    "autoApply": false
  }
}
```

Version 1: Updates nur auf expliziten API-Aufruf.

---

## J. Update Check

`POST /api/v1/instances/{id}/updates/check`

prüft:

1. installierte Image-Referenz
2. Zielversion/Digest aus konfigurierter Registry oder Releasequelle
3. Digest-Vergleich
4. nginx-Template-Version
5. Ergebnis ohne Änderungen

Beispiel:

```json
{
  "licenseServer": {
    "installedVersion": "0.4.1",
    "availableVersion": "0.4.2",
    "installedDigest": "sha256:aaa",
    "availableDigest": "sha256:bbb",
    "updateAvailable": true
  },
  "nginx": {
    "installedTemplateVersion": 2,
    "availableTemplateVersion": 3,
    "updateAvailable": true
  }
}
```

---

## K. LicenseServer Update Workflow

```text
Preflight
   ↓
MySQL prüfen falls nötig
   ↓
Zielversion bestimmen
   ↓
Image pullen
   ↓
Digest prüfen
   ↓
Pre-Update-Backup
   ↓
bestehende Containerdefinition sichern
   ↓
alten Container stoppen
   ↓
neuen Container mit identischen persistenten Daten erstellen
   ↓
starten
   ↓
/health
   ↓
erfolgreich?
   ├── ja → commit
   └── nein → neuen entfernen, alten Stand wiederherstellen
```

Persistente Daten niemals beim Update löschen.

---

## L. Update Request

```json
{
  "targetVersion": "0.4.2",
  "createBackup": true
}
```

Optional:

```json
{
  "targetDigest": "sha256:...",
  "allowDowngrade": false,
  "createBackup": true
}
```

Defaults:

```text
createBackup=true
allowDowngrade=false
```

---

## M. Kombiniertes Update

```http
POST /api/v1/instances/{id}/updates/all
```

Reihenfolge:

```text
Preflight
   ↓
Backup
   ↓
LicenseServer Update
   ↓
Healthcheck
   ↓
nginx Template Update
   ↓
nginx -t
   ↓
reload
   ↓
Healthcheck
```

---

## N. Update-Historie

Neue Tabelle:

```text
updates
-------
id
instance_id
component
from_version
to_version
from_digest
to_digest
status
started_at
completed_at
error
```

Komponenten:

```text
license-server
nginx-template
agent
```

Stati:

```text
pending
checking
downloading
backing_up
applying
verifying
completed
failed
rolled_back
```

---

## O. Vollständiger Instanz-Reset

Ein vollständiger Reset muss möglich sein.

Dieser Reset betrifft standardmäßig **genau eine Instanz**, nicht den gesamten Server.

Endpunkt:

```http
POST /api/v1/instances/{id}/reset
```

Request:

```json
{
  "mode": "full",
  "createBackup": true,
  "confirmation": "RESET"
}
```

### Full Reset umfasst

```text
Instanz stoppen
   ↓
optional finales Backup
   ↓
Docker Container entfernen
   ↓
nginx Config entfernen
   ↓
nginx -t
   ↓
nginx reload
   ↓
Port freigeben
   ↓
lokale Instanzdaten löschen
   ↓
SQLite DB löschen, falls lokal
   ↓
lokale Secrets/Keys löschen
   ↓
Backup-Historie je nach Option behandeln
   ↓
Instanz-Metadaten entfernen/deaktivieren
```

### Externes MySQL

Bei externem MySQL darf ein Full Reset **nicht automatisch die externe Datenbank oder Tabellen löschen**.

Standard:

```text
MySQL-Verbindungsdaten lokal entfernen
externe DB unverändert lassen
```

Optional kann später ein explizites DB-Purge eingeführt werden, aber nur mit eigener separater Bestätigung.

### Backup-Optionen

Request erweitert:

```json
{
  "mode": "full",
  "createBackup": true,
  "deleteBackups": false,
  "confirmation": "RESET"
}
```

Standard:

```text
createBackup=true
deleteBackups=false
```

Damit bleiben bestehende Backups nach einem Reset erhalten.

### Sicherheitsregeln

- Reset niemals via GET
- explizite Confirmation erforderlich
- Instanz-ID und Name im Audit-Log
- Secrets nicht loggen
- kein Wildcard-Reset
- keine implizite serverweite Löschung

---

## P. Reset-Arten

Optional zusätzlich:

```http
POST /api/v1/instances/{id}/reset
```

mit:

```json
{
  "mode": "runtime"
}
```

`runtime`:

```text
Container entfernen
nginx neu generieren
Container neu erstellen
persistente Daten behalten
```

`full`:

```text
Container löschen
nginx löschen
lokale persistente Instanzdaten löschen
Secrets löschen
Port freigeben
Metadaten löschen/deaktivieren
```

Damit können beschädigte Runtime-Zustände repariert werden, ohne direkt Daten zu vernichten.

---

## Q. Host-/Agent-Reset

Ein Reset des kompletten Instanz-Agent-Hosts ist eine getrennte Funktion und darf **nicht** mit Instanz-Reset vermischt werden.

Falls später benötigt:

```http
POST /api/v1/agent/reset
```

muss dafür wesentlich strengere Schutzmaßnahmen besitzen.

Für Version 1:

```text
Nicht implementieren.
```

Nur Instanz-Reset implementieren.

---

## R. Agent Update Status

Status-Endpunkte:

```http
GET /api/v1/agent/version
GET /api/v1/agent/update
```

Beispiel:

```json
{
  "installedVersion": "0.3.0",
  "availableVersion": "0.4.0",
  "updateAvailable": true,
  "selfUpdateSupported": false
}
```

Agent-Selbstupdate erst später.

---

## S. Zusätzliche Fehlercodes

```text
MYSQL_PREFLIGHT_FAILED
MYSQL_UNAVAILABLE
NGINX_NOT_INSTALLED
NGINX_VERSION_UNSUPPORTED
NGINX_SYNTAX_INVALID
NGINX_TEMPLATE_OUTDATED
NGINX_TEMPLATE_UPDATE_FAILED
IMAGE_UPDATE_NOT_AVAILABLE
IMAGE_PULL_FAILED
IMAGE_DIGEST_MISMATCH
PRE_UPDATE_BACKUP_FAILED
LICENSE_SERVER_UPDATE_FAILED
LICENSE_SERVER_UPDATE_ROLLBACK_FAILED
UPDATE_TARGET_INVALID
DOWNGRADE_NOT_ALLOWED
RESET_CONFIRMATION_REQUIRED
RESET_BACKUP_FAILED
RESET_FAILED
```

---

## T. Zusätzliche Definition of Done

- MySQL wird bei Create geprüft
- MySQL wird bei Start geprüft
- MySQL-Checks besitzen Timeouts
- nginx wird vor Apply/Reload mit `nginx -t` geprüft
- nginx-Templates sind versioniert
- veraltete nginx-Configs werden erkannt
- nginx-Configs können aktualisiert werden
- LicenseServer-Updates können geprüft werden
- LicenseServer-Container können aktualisiert werden
- Pre-Update-Backup standardmäßig aktiv
- fehlgeschlagenes Update rollbackfähig
- Update-Historie vorhanden
- vollständiger Instanz-Reset vorhanden
- Full Reset entfernt Container/nginx/Port/lokale Daten/Secrets
- externe MySQL-Datenbank wird beim Reset standardmäßig nicht gelöscht
- bestehende Backups bleiben standardmäßig erhalten
