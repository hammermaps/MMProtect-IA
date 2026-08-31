using MmProtect.InstanceAgent.Application.Instances;

namespace MmProtect.InstanceAgent.Api;

public static class InstanceUpdateEndpoints
{
    public static IEndpointRouteBuilder MapInstanceUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/instances/{id}/updates");
        group.MapGet("", GetAsync);
        group.MapPost("/check", GetAsync);
        group.MapPost("/license-server", ApplyLicenseServerAsync);
        group.MapPost("/nginx", ApplyNginxAsync);
        group.MapPost("/all", NotImplemented);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(string id, HttpContext context, IInstanceUpdateService updates, CancellationToken cancellationToken)
    {
        var result = await updates.CheckAsync(id, cancellationToken);
        return result.Succeeded
            ? Results.Ok(new
            {
                licenseServer = new { currentImage = result.CurrentImage, availableImage = result.AvailableImage, updateAvailable = result.LicenseServerUpdateAvailable },
                nginx = new { installedTemplateVersion = result.InstalledNginxTemplateVersion, availableTemplateVersion = result.AvailableNginxTemplateVersion, updateAvailable = result.NginxUpdateAvailable }
            })
            : AgentProblem.Create(context, 404, result.ErrorCode!, "The requested instance does not exist.");
    }

    private static IResult NotImplemented(HttpContext context) =>
        AgentProblem.Create(context, 501, "INSTANCE_UPDATE_NOT_IMPLEMENTED", "Update application is not implemented yet; use the check endpoint before planning maintenance.");

    private static async Task<IResult> ApplyLicenseServerAsync(string id, HttpContext context, IInstanceUpdateService updates, CancellationToken cancellationToken)
    {
        var result = await updates.ApplyLicenseServerAsync(id, cancellationToken);
        return result.Succeeded ? Results.Ok(new { id, status = "updated" }) : AgentProblem.Create(context, result.ErrorCode == "INSTANCE_NOT_FOUND" ? 404 : 409, result.ErrorCode!, "LicenseServer update could not be applied.");
    }

    private static async Task<IResult> ApplyNginxAsync(string id, HttpContext context, IInstanceUpdateService updates, CancellationToken cancellationToken)
    {
        var result = await updates.ApplyNginxAsync(id, cancellationToken);
        return result.Succeeded
            ? Results.Ok(new { id, nginx = new { templateVersion = result.TemplateVersion, configRevision = result.ConfigRevision } })
            : AgentProblem.Create(context, result.ErrorCode == "INSTANCE_NOT_FOUND" ? 404 : 409, result.ErrorCode!, "nginx update could not be applied.");
    }
}
