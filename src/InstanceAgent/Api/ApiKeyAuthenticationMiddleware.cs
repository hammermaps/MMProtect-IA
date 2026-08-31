using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MmProtect.InstanceAgent.Options;

namespace MmProtect.InstanceAgent.Api;

public sealed class ApiKeyAuthenticationMiddleware(RequestDelegate next, IOptions<AgentOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var configuredKey = options.Value.ApiKey;
        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";

        if (string.IsNullOrWhiteSpace(configuredKey) ||
            !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !FixedTimeEquals(authorization[prefix.Length..], configuredKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new AgentProblem.AgentErrorResponse(
                "AUTH_REQUIRED",
                "A valid bearer token is required.",
                context.TraceIdentifier));
            return;
        }

        await next(context);
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }
}
