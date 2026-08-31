namespace MmProtect.InstanceAgent.Api;

public static class AgentProblem
{
    public static IResult Create(HttpContext context, int statusCode, string error, string message) =>
        Results.Json(
            new AgentErrorResponse(error, message, context.TraceIdentifier),
            statusCode: statusCode);

    public sealed record AgentErrorResponse(string Error, string Message, string TraceId);
}
