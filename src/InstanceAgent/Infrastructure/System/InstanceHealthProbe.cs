namespace MmProtect.InstanceAgent.Infrastructure.System;

public interface IInstanceHealthProbe
{
    Task<bool> IsHealthyAsync(int hostPort, CancellationToken cancellationToken);
}

public sealed class InstanceHealthProbe(IHttpClientFactory httpClientFactory) : IInstanceHealthProbe
{
    public async Task<bool> IsHealthyAsync(int hostPort, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var client = httpClientFactory.CreateClient(nameof(InstanceHealthProbe));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{hostPort}/health", timeout.Token);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) when (!timeout.IsCancellationRequested)
            {
                // The container may still be starting. Retry until the bounded timeout expires.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token); }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }
}
