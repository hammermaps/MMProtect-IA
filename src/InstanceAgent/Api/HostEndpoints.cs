using MmProtect.InstanceAgent.Application.Host;
using MmProtect.InstanceAgent.Infrastructure.Docker;
using MmProtect.InstanceAgent.Options;
using Microsoft.Extensions.Options;

namespace MmProtect.InstanceAgent.Api;

public static class HostEndpoints
{
    public static IEndpointRouteBuilder MapHostEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/host");
        group.MapGet("", GetHostAsync);
        group.MapGet("/health", GetHealthAsync);
        group.MapGet("/capacity", GetCapacity);
        return endpoints;
    }

    private static async Task<IResult> GetHostAsync(
        IInstanceAgentIdentityService identityService,
        IDockerService dockerService,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var identity = await identityService.GetOrCreateAsync(cancellationToken);
        var docker = await dockerService.GetAvailabilityAsync(cancellationToken);
        var nginxAvailable = File.Exists("/usr/sbin/nginx") || File.Exists("/usr/bin/nginx");
        return Results.Ok(new
        {
            instanceAgentId = identity,
            hostname = Environment.MachineName,
            agentVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            apiVersion = "v1",
            environment = environment.EnvironmentName,
            docker = new { available = docker.Available, version = docker.ServerVersion, errorCode = docker.ErrorCode },
            nginx = new { available = nginxAvailable },
            status = docker.Available && nginxAvailable ? "healthy" : "degraded"
        });
    }

    private static async Task<IResult> GetHealthAsync(
        IInstanceAgentIdentityService identityService,
        IDockerService dockerService,
        CancellationToken cancellationToken)
    {
        var identity = await identityService.GetOrCreateAsync(cancellationToken);
        var docker = await dockerService.GetAvailabilityAsync(cancellationToken);
        var nginxAvailable = File.Exists("/usr/sbin/nginx") || File.Exists("/usr/bin/nginx");
        return Results.Ok(new
        {
            instanceAgentId = identity,
            status = docker.Available && nginxAvailable ? "healthy" : "degraded",
            checks = new
            {
                metadataStore = "healthy",
                docker = docker.Available ? "healthy" : "unavailable",
                nginx = nginxAvailable ? "healthy" : "unavailable"
            }
        });
    }

    private static IResult GetCapacity(IOptions<AgentOptions> options)
    {
        var root = new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory)!);
        return Results.Ok(new
        {
            cpu = new { logicalProcessors = Environment.ProcessorCount },
            memory = new { totalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes },
            disk = new { totalBytes = root.TotalSize, freeBytes = root.AvailableFreeSpace },
            ports = new { rangeStart = options.Value.PortRangeStart, rangeEnd = options.Value.PortRangeEnd }
        });
    }
}
