using MmProtect.InstanceAgent.Infrastructure.Persistence;

namespace MmProtect.InstanceAgent.Application.Host;

public interface IInstanceAgentIdentityService
{
    Task<string> GetOrCreateAsync(CancellationToken cancellationToken);
}

public sealed class InstanceAgentIdentityService(IAgentDatabase database) : IInstanceAgentIdentityService
{
    public async Task<string> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var existing = await database.GetAgentIdAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var candidate = $"ia-{Guid.NewGuid():N}";
        await database.StoreAgentIdAsync(candidate, cancellationToken);
        return await database.GetAgentIdAsync(cancellationToken) ?? candidate;
    }
}
