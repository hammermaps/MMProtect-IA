using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;
using MmProtect.InstanceAgent.Options;

namespace MmProtect.InstanceAgent.Infrastructure.Docker;

public interface IDockerService
{
    Task<DockerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken);

    Task<DockerOperationResult> CreateAsync(CreateLicenseServerContainer request, CancellationToken cancellationToken);

    Task<DockerOperationResult> StartAsync(string containerId, CancellationToken cancellationToken);

    Task<DockerOperationResult> StopAsync(string containerId, CancellationToken cancellationToken);

    Task<DockerOperationResult> RestartAsync(string containerId, CancellationToken cancellationToken);

    Task<DockerOperationResult> DeleteAsync(string containerId, CancellationToken cancellationToken);
}

public sealed record DockerAvailability(bool Available, string? ServerVersion, string? ErrorCode);

public sealed record DockerOperationResult(bool Succeeded, string? ContainerId, string? ErrorCode)
{
    public static DockerOperationResult Failure(string errorCode) => new(false, null, errorCode);

    public static DockerOperationResult Success(string? containerId = null) => new(true, containerId, null);
}

public sealed record CreateLicenseServerContainer(
    string ContainerName,
    string ImageReference,
    int HostPort,
    IReadOnlyCollection<string> Environment,
    IReadOnlyDictionary<string, string> BindMounts);

public sealed class DockerService : IDockerService, IDisposable
{
    private readonly DockerClient _client;
    private readonly ILogger<DockerService> _logger;

    public DockerService(IOptions<AgentOptions> options, ILogger<DockerService> logger)
    {
        _logger = logger;
        var configuration = new DockerClientConfiguration(
            new Uri(options.Value.DockerEndpoint),
            defaultTimeout: TimeSpan.FromSeconds(options.Value.DockerTimeoutSeconds));
        _client = configuration.CreateClient();
    }

    public async Task<DockerAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = await _client.System.GetVersionAsync(cancellationToken);
            return new DockerAvailability(true, version.Version, null);
        }
        catch (TaskCanceledException)
        {
            return new DockerAvailability(false, null, "DOCKER_TIMEOUT");
        }
        catch (DockerApiException)
        {
            return new DockerAvailability(false, null, "DOCKER_UNAVAILABLE");
        }
        catch (HttpRequestException)
        {
            return new DockerAvailability(false, null, "DOCKER_UNAVAILABLE");
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Docker availability check failed with {ExceptionType}", exception.GetType().Name);
            return new DockerAvailability(false, null, "DOCKER_UNAVAILABLE");
        }
    }

    public async Task<DockerOperationResult> CreateAsync(CreateLicenseServerContainer request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = request.ContainerName,
                Image = request.ImageReference,
                // Bind-mounted instance files are owned by the agent's own uid (root); run the
                // container as the same uid so it can read/write them regardless of the image's USER.
                User = "0:0",
                Env = request.Environment.ToList(),
                ExposedPorts = new Dictionary<string, EmptyStruct> { ["8080/tcp"] = default },
                HostConfig = new HostConfig
                {
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        ["8080/tcp"] = [new PortBinding { HostIP = "127.0.0.1", HostPort = request.HostPort.ToString() }]
                    },
                    Binds = request.BindMounts.Select(mount => $"{mount.Key}:{mount.Value}").ToList(),
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped }
                }
            }, cancellationToken);

            _logger.LogInformation("Created Docker container {ContainerId}", response.ID);
            return DockerOperationResult.Success(response.ID);
        }
        catch (TaskCanceledException)
        {
            return DockerOperationResult.Failure("DOCKER_TIMEOUT");
        }
        catch (DockerApiException)
        {
            return DockerOperationResult.Failure("DOCKER_CREATE_FAILED");
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Docker create failed with {ExceptionType}", exception.GetType().Name);
            return DockerOperationResult.Failure("DOCKER_CREATE_FAILED");
        }
    }

    public Task<DockerOperationResult> StartAsync(string containerId, CancellationToken cancellationToken) =>
        RunAsync(containerId, "start", () => _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), cancellationToken), cancellationToken);

    public Task<DockerOperationResult> StopAsync(string containerId, CancellationToken cancellationToken) =>
        RunAsync(containerId, "stop", () => _client.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 15 }, cancellationToken), cancellationToken);

    public Task<DockerOperationResult> RestartAsync(string containerId, CancellationToken cancellationToken) =>
        RunAsync(containerId, "restart", () => _client.Containers.RestartContainerAsync(containerId, new ContainerRestartParameters { WaitBeforeKillSeconds = 15 }, cancellationToken), cancellationToken);

    public async Task<DockerOperationResult> DeleteAsync(string containerId, CancellationToken cancellationToken)
    {
        try
        {
            await _client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }, cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone (e.g. a previous delete attempt removed the container but failed at a later
            // step). Deletion must be idempotent, so treat "not found" as success instead of blocking
            // cleanup forever on retry.
            _logger.LogInformation("Docker delete: container {ContainerId} already absent", containerId);
            return DockerOperationResult.Success(containerId);
        }
        catch (TaskCanceledException)
        {
            return DockerOperationResult.Failure("DOCKER_TIMEOUT");
        }
        catch (DockerApiException)
        {
            return DockerOperationResult.Failure("DOCKER_DELETE_FAILED");
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Docker delete failed with {ExceptionType}", exception.GetType().Name);
            return DockerOperationResult.Failure("DOCKER_DELETE_FAILED");
        }

        _logger.LogInformation("Docker delete completed for container {ContainerId}", containerId);
        return DockerOperationResult.Success(containerId);
    }

    public void Dispose() => _client.Dispose();

    private async Task<DockerOperationResult> RunAsync(string containerId, string operation, Func<Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await action();
            _logger.LogInformation("Docker {Operation} completed for container {ContainerId}", operation, containerId);
            return DockerOperationResult.Success(containerId);
        }
        catch (TaskCanceledException)
        {
            return DockerOperationResult.Failure("DOCKER_TIMEOUT");
        }
        catch (DockerApiException)
        {
            return DockerOperationResult.Failure($"DOCKER_{operation.ToUpperInvariant()}_FAILED");
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Docker {Operation} failed with {ExceptionType}", operation, exception.GetType().Name);
            return DockerOperationResult.Failure($"DOCKER_{operation.ToUpperInvariant()}_FAILED");
        }
    }
}
