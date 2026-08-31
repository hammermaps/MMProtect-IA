using MmProtect.InstanceAgent.Api;
using MmProtect.InstanceAgent.Application.Host;
using MmProtect.InstanceAgent.Application.Backups;
using MmProtect.InstanceAgent.Application.Instances;
using MmProtect.InstanceAgent.Domain.Instances;
using MmProtect.InstanceAgent.Infrastructure.Docker;
using MmProtect.InstanceAgent.Infrastructure.Database;
using MmProtect.InstanceAgent.Infrastructure.Backup;
using MmProtect.InstanceAgent.Infrastructure.LicenseServer;
using MmProtect.InstanceAgent.Infrastructure.Nginx;
using MmProtect.InstanceAgent.Infrastructure.Tls;
using MmProtect.InstanceAgent.Infrastructure.Persistence;
using MmProtect.InstanceAgent.Infrastructure.Security;
using MmProtect.InstanceAgent.Infrastructure.System;
using MmProtect.InstanceAgent.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection(AgentOptions.SectionName))
    .Validate(options => options.PortRangeStart is > 0 and <= 65535, "Port range start must be between 1 and 65535.")
    .Validate(options => options.PortRangeEnd is > 0 and <= 65535, "Port range end must be between 1 and 65535.")
    .Validate(options => options.PortRangeStart <= options.PortRangeEnd, "Port range start must not exceed port range end.")
    .Validate(options => Uri.TryCreate(options.DockerEndpoint, UriKind.Absolute, out _), "Docker endpoint must be an absolute URI.")
    .Validate(options => options.DockerTimeoutSeconds is > 0 and <= 120, "Docker timeout must be between 1 and 120 seconds.")
    .Validate(options => options.RestoreMaxArchiveBytes is > 0 and <= 10L * 1024 * 1024 * 1024, "Restore archive limit must be between 1 byte and 10 GiB.")
    .Validate(options => options.RestoreMaxExtractedBytes >= options.RestoreMaxArchiveBytes && options.RestoreMaxExtractedBytes <= 50L * 1024 * 1024 * 1024, "Restore extracted limit must be at least the archive limit and no more than 50 GiB.")
    .ValidateOnStart();

builder.Services.AddSingleton<IAgentDatabase, AgentDatabase>();
builder.Services.AddSingleton<IInstanceAgentIdentityService, InstanceAgentIdentityService>();
builder.Services.AddSingleton<IInstanceEventService, InstanceEventService>();
builder.Services.AddSingleton<IDomainNameValidator, DomainNameValidator>();
builder.Services.AddSingleton<IInstanceRepository, InstanceRepository>();
builder.Services.AddSingleton<IIdempotencyService, IdempotencyService>();
builder.Services.AddSingleton<IInstanceLifecycleService, InstanceLifecycleService>();
builder.Services.AddSingleton<IInstanceDeletionService, InstanceDeletionService>();
builder.Services.AddSingleton<IInstanceResetService, InstanceResetService>();
builder.Services.AddSingleton<IInstanceUpdateService, InstanceUpdateService>();
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<INginxService, NginxService>();
builder.Services.AddSingleton<ITlsCertificateService, TlsCertificateService>();
builder.Services.AddSingleton<IMySqlPreflightService, MySqlPreflightService>();
builder.Services.AddSingleton<IMySqlConnectionStringFactory, MySqlConnectionStringFactory>();
builder.Services.AddSingleton<ISqliteBackupProvider, SqliteBackupProvider>();
builder.Services.AddSingleton<IMySqlDumpProvider, MySqlDumpProvider>();
builder.Services.AddSingleton<IZipBackupWriter, ZipBackupWriter>();
builder.Services.AddSingleton<IInstanceBackupService, InstanceBackupService>();
builder.Services.AddSingleton<IRestoreArchiveValidator, RestoreArchiveValidator>();
builder.Services.AddSingleton<IInstanceRestoreService, InstanceRestoreService>();
builder.Services.AddSingleton<ISqliteInstanceProvisioningService, SqliteInstanceProvisioningService>();
builder.Services.AddSingleton<IMySqlInstanceProvisioningService, MySqlInstanceProvisioningService>();
builder.Services.AddSingleton<IInstanceSecretStore, InstanceSecretStore>();
builder.Services.AddSingleton<ISqliteLicenseServerDatabaseInitializer, SqliteLicenseServerDatabaseInitializer>();
builder.Services.AddSingleton<ILocalPortProbe, LocalPortProbe>();
builder.Services.AddHttpClient(nameof(InstanceHealthProbe));
builder.Services.AddSingleton<IInstanceHealthProbe, InstanceHealthProbe>();
builder.Services.AddSingleton<IPortAllocator, PortAllocator>();
builder.Services.AddSingleton<IDockerService, DockerService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<IAgentDatabase>();
    await database.InitializeAsync(app.Lifetime.ApplicationStopping);
    var identity = scope.ServiceProvider.GetRequiredService<IInstanceAgentIdentityService>();
    await identity.GetOrCreateAsync(app.Lifetime.ApplicationStopping);
}

app.UseMiddleware<ApiKeyAuthenticationMiddleware>();
app.MapHostEndpoints();
app.MapInstanceEndpoints();
app.MapInstanceUpdateEndpoints();
app.MapInstanceEventEndpoints();
app.MapPreflightEndpoints();
app.MapBackupEndpoints();
app.MapAgentEndpoints();

app.Run();

public partial class Program;
