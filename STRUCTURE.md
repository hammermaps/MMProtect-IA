# MMProtect – verbindliche Strukturplanung

## Systemebenen

```text
Instanzmanager
   |
   | LAN / WireGuard
   |
   +--> Server 1: Instanz-Agent + Docker + nginx
   |       +--> Instanz A
   |       +--> Instanz B
   |
   +--> Server 2: Instanz-Agent + Docker + nginx
   |       +--> Instanz C
   |       +--> Instanz D
   |
   +--> Server n: Instanz-Agent + Docker + nginx
```

Der Instanzmanager wird separat entwickelt.

Dieses Repository-Paket plant und beschreibt primär den **Instanz-Agent**.

# MMProtect Instanz-Agent – geplante Repository-Struktur

```text
MMProtect/
├── AGENTS.md
├── README.md
├── Dockerfile
├── docker-compose.yml
├── docker-compose.sqlite.yml
├── docs/
│   ├── docker-deployment.md
│   ├── instance-agent-plan.md
│   └── instance-agent.md
│
├── deploy/
│   └── instance-agent/
│       ├── install.sh
│       ├── mmprotect-instance-agent.service
│       ├── nginx/
│       │   └── mmprotect-include.conf
│       └── examples/
│           └── appsettings.Production.json
│
└── src/
    ├── AdminUi/
    ├── EncoderCli/
    ├── EncoderCli.Tests/
    ├── EncoderGui/
    ├── LicenseServer/
    ├── LicenseServer.Tests/
    ├── PhpDecoderLoader/
    ├── InstanceAgent/
    │   ├── Api/
    │   │   ├── BackupEndpoints.cs
    │   │   ├── HostEndpoints.cs
    │   │   └── InstanceEndpoints.cs
    │   │
    │   ├── Application/
    │   │   ├── Backups/
    │   │   ├── Host/
    │   │   └── Instances/
    │   │
    │   ├── Domain/
    │   │   ├── Backups/
    │   │   ├── Instances/
    │   │   └── Ports/
    │   │
    │   ├── Infrastructure/
    │   │   ├── Backup/
    │   │   │   ├── BackupService.cs
    │   │   │   ├── MySqlBackupProvider.cs
    │   │   │   ├── SqliteBackupProvider.cs
    │   │   │   └── ZipBackupWriter.cs
    │   │   │
    │   │   ├── Docker/
    │   │   │   ├── DockerOptions.cs
    │   │   │   └── DockerService.cs
    │   │   │
    │   │   ├── Nginx/
    │   │   │   ├── NginxOptions.cs
    │   │   │   ├── NginxService.cs
    │   │   │   └── NginxTemplateRenderer.cs
    │   │   │
    │   │   ├── Persistence/
    │   │   │   ├── AgentDbContext.cs
    │   │   │   └── Migrations/
    │   │   │
    │   │   ├── Security/
    │   │   │   ├── AgentAuthentication.cs
    │   │   │   ├── KeyGenerator.cs
    │   │   │   └── SecretStore.cs
    │   │   │
    │   │   └── System/
    │   │       ├── PortAllocator.cs
    │   │       └── ProcessRunner.cs
    │   │
    │   ├── Models/
    │   │   ├── Requests/
    │   │   └── Responses/
    │   │
    │   ├── Program.cs
    │   ├── appsettings.json
    │   └── InstanceAgent.csproj
    │
    ├── InstanceAgent.Tests/
    │   ├── Fixtures/
    │   ├── Integration/
    │   ├── Unit/
    │   └── InstanceAgent.Tests.csproj
    │
    └── MmProtect.sln
```

## Laufzeit-Dateisystem

```text
/var/lib/mmprotect-agent/
├── agent.db
├── agent-id
├── instances/
│   └── <instance-id>/
│       ├── instance.json
│       ├── data/
│       ├── keys/
│       │   ├── signing-private.pem
│       │   └── signing-public.pem
│       └── secrets/
│
├── backups/
│   └── <instance-id>/
│       └── *.zip
│
└── tmp/
```

nginx:

```text
/etc/nginx/mmprotect.d/
└── <instance-id>.conf
```

## Wichtig

Der `InstanceAgent` ist ausschließlich ein Host-Agent.

Multi-Host-Logik gehört in eine spätere, separate zentrale Verwaltungsinstanz.


## Zusätzliche Module für Preflight, Updates und Reset

```text
src/InstanceAgent/
├── Application/
│   ├── Preflight/
│   ├── Updates/
│   └── Reset/
│
├── Infrastructure/
│   ├── Database/
│   │   └── MySqlPreflightService.cs
│   ├── Nginx/
│   │   ├── NginxVersionService.cs
│   │   └── NginxTemplateMigrationService.cs
│   ├── Updates/
│   │   ├── LicenseServerUpdateService.cs
│   │   ├── UpdateCheckService.cs
│   │   └── UpdateRollbackService.cs
│   └── Reset/
│       └── InstanceResetService.cs
│
└── Domain/
    └── Updates/
```

Neue Persistenzobjekte:

```text
updates
idempotency_records
```

Reset ist instanzbezogen. Ein kompletter Agent-/Host-Reset gehört nicht in Version 1.
