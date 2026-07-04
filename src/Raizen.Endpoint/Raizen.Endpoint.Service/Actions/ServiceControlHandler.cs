using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Starts, stops, or restarts a Windows service by name.
///
/// Required parameters:
///   ServiceName — Windows service name (not display name)
///
/// Security: uses System.ServiceProcess.ServiceController — no shell.
/// </summary>
public sealed class ServiceControlHandler(ActionType actionType, ILogger<ServiceControlHandler> log)
    : IActionHandler, IPreflightCheck
{
    // Service names: only alphanumeric, underscore, hyphen, dot
    private static readonly Regex SafeServiceName = new(
        @"^[A-Za-z0-9_.\-]{1,256}$", RegexOptions.None, TimeSpan.FromSeconds(1));

    public ActionType HandledType => actionType;

    public string? Validate(ElevationRequestDto request)
    {
        var serviceName = request.Parameters.GetValueOrDefault("ServiceName", "");
        if (string.IsNullOrWhiteSpace(serviceName)) return "ServiceName parameter is required.";
        var exists = ServiceController.GetServices()
            .Any(s => string.Equals(s.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));
        return exists ? null : $"Service '{serviceName}' does not exist on this machine.";
    }

    public async Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        var serviceName = request.Parameters.GetValueOrDefault("ServiceName", "");

        if (!SafeServiceName.IsMatch(serviceName))
            return new ActionResult(false, ErrorMessage: $"ServiceName '{serviceName}' is invalid.");

        try
        {
            using var sc = new ServiceController(serviceName);
            var displayName = sc.DisplayName;

            switch (actionType)
            {
                case ActionType.StartService:
                    if (sc.Status == ServiceControllerStatus.Running)
                        return new ActionResult(true, ResultMessage: $"Service '{displayName}' is already running.");
                    sc.Start();
                    await WaitForStatusAsync(sc, ServiceControllerStatus.Running, ct);
                    log.LogInformation("[Request:{RequestId}] Started service '{Service}'", request.Id, serviceName);
                    return new ActionResult(true, ResultMessage: $"Started service '{displayName}'.");

                case ActionType.StopService:
                    if (!sc.CanStop)
                        return new ActionResult(false, ErrorMessage: $"Service '{displayName}' cannot be stopped.");
                    if (sc.Status == ServiceControllerStatus.Stopped)
                        return new ActionResult(true, ResultMessage: $"Service '{displayName}' is already stopped.");
                    sc.Stop();
                    await WaitForStatusAsync(sc, ServiceControllerStatus.Stopped, ct);
                    log.LogInformation("[Request:{RequestId}] Stopped service '{Service}'", request.Id, serviceName);
                    return new ActionResult(true, ResultMessage: $"Stopped service '{displayName}'.");

                case ActionType.RestartService:
                    if (!sc.CanStop)
                        return new ActionResult(false, ErrorMessage: $"Service '{displayName}' cannot be stopped for restart.");
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        await WaitForStatusAsync(sc, ServiceControllerStatus.Stopped, ct);
                    }
                    sc.Start();
                    await WaitForStatusAsync(sc, ServiceControllerStatus.Running, ct);
                    log.LogInformation("[Request:{RequestId}] Restarted service '{Service}'", request.Id, serviceName);
                    return new ActionResult(true, ResultMessage: $"Restarted service '{displayName}'.");

                default:
                    return new ActionResult(false, ErrorMessage: $"Unexpected action type {actionType}.");
            }
        }
        catch (InvalidOperationException ex)
        {
            log.LogError(ex, "[Request:{RequestId}] Service control error.", request.Id);
            return new ActionResult(false, ErrorMessage: "Service control error. See endpoint logs for details.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Request:{RequestId}] Service control failed.", request.Id);
            return new ActionResult(false, ErrorMessage: "Service control operation failed. See endpoint logs for details.");
        }
    }

    private static async Task WaitForStatusAsync(
        ServiceController sc, ServiceControllerStatus desired, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (sc.Status != desired)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
                throw new System.TimeoutException($"Service did not reach state {desired} within 60 seconds.");
            await Task.Delay(500, ct);
            try
            {
                sc.Refresh();
            }
            catch (InvalidOperationException)
            {
                // Service may have been deleted or become inaccessible
                throw new InvalidOperationException(
                    $"Service is no longer accessible while waiting for state {desired}.");
            }
        }
    }
}
