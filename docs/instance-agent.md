# MMProtect Instanz-Agent

Der Instanz-Agent verwaltet ausschließlich lokale MMProtect-LicenseServer-Container. Eine spätere zentrale Mehrserver-Verwaltung ist der Instanzmanager und kommuniziert über LAN oder WireGuard.

## Konfiguration

Setze Secrets per Umgebung, nicht in `appsettings.json`:

```text
InstanceAgent__ApiKey=<zufaelliger-agent-token>
InstanceAgent__LicenseServerImage=registry.example/mmprotect-license-server:1.0.0
InstanceAgent__DataDirectory=/var/lib/mmprotect-agent
InstanceAgent__NginxConfigurationDirectory=/etc/nginx/mmprotect.d
InstanceAgent__LetsEncryptEmail=admin@example.de
```

`LicenseServerImage` muss einen konkreten Tag oder Digest besitzen; `latest` wird abgelehnt. Der Docker-Socket wird standardmäßig über `unix:///var/run/docker.sock` verwendet. Der Dienstaccount benötigt Zugriff darauf sowie Schreibrechte auf die Daten- und nginx-Verzeichnisse.

## Sicherheit

- Alle API-Endpunkte benötigen `Authorization: Bearer <Agent-Token>`.
- Containerports werden ausschließlich an `127.0.0.1` veröffentlicht.
- Agent- und Instanzsecrets liegen nur in lokalen Dateien mit `0600`.
- API-Keys werden ausschließlich einmalig in der Create-Antwort ausgegeben.
- MySQL-Passwörter werden nie in der Agent-Datenbank gespeichert.

## TLS

TLS ist pro Instanz optional. Ohne TLS wird HTTP reverse-proxied. Mit `tls.mode: "letsencrypt"` wird zuerst der HTTP-01-Challenge-vHost aktiviert, danach Certbot ausgeführt und abschließend HTTPS aktiviert.

## API

Vorhandene Kernbereiche:

- Host: `/api/v1/host`, `/health`, `/capacity`
- Instanzen: Create, List, Detail, Start, Stop, Restart, Delete
- Preflight: Docker, nginx und MySQL
- Backups: Create, List, Detail, Download und Delete

`Idempotency-Key` wird für Instanz- und Backup-Erstellung unterstützt. Backup-Downloads sind Streaming-Dateiantworten mit Range-Support.

## Backups und Restore

SQLite wird über die SQLite-Backup-API gesichert. ZIPs enthalten ein versioniertes `manifest.json`, Instanzmetadaten, Daten, Datenbanksnapshot und ausschließlich den öffentlichen Signing-Key. Ein SQLite-Restore legt zuerst ein Pre-Restore-Backup an, tauscht das Datenverzeichnis atomar und prüft nach einem Neustart den lokalen Health-Endpunkt. MySQL-Dumps und -Restore sind noch in Umsetzung.

`RestoreMaxArchiveBytes` (standardmäßig 1 GiB) begrenzt den Upload; `RestoreMaxExtractedBytes` (standardmäßig 5 GiB) begrenzt die entpackte Nutzlast. Beide Werte gehören in den Abschnitt `InstanceAgent` der Konfiguration.

## Troubleshooting

Prüfe zuerst `/api/v1/preflight/docker` und `/api/v1/preflight/nginx`. Bei MySQL zusätzlich `POST /api/v1/preflight/mysql`. nginx wird nur nach erfolgreichem `nginx -t` neu geladen.

## Deployment

Beispiele für den systemd-Service, die Produktionskonfiguration und den nginx-Include liegen unter `deploy/instance-agent/`. Die Datei `/etc/mmprotect-instance-agent/environment` enthält mindestens den Agent-Token, das konkrete LicenseServer-Image und optional die Let's-Encrypt-E-Mail; sie muss `0600` erhalten.

Nach dem Publish der Anwendung nach `/opt/mmprotect-instance-agent` installiert `sudo deploy/instance-agent/install.sh` die Systemartefakte. Der Account `mmprotect-agent` benötigt Gruppenmitgliedschaft für den Docker-Socket, beispielsweise über `usermod -aG docker mmprotect-agent`, sofern dessen lokale Sicherheitsrichtlinie dies zulässt.
