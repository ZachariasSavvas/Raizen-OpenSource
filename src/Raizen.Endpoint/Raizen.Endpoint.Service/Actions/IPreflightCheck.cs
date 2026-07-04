using Raizen.Shared.DTOs;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Optional extension for <see cref="IActionHandler"/> implementations that can
/// validate preconditions cheaply before execution begins.
///
/// <see cref="ElevationWorker"/> calls <see cref="Validate"/> before
/// <see cref="IActionHandler.ExecuteAsync"/>. If an error string is returned
/// the request is reported as failed immediately — no execution occurs.
/// </summary>
public interface IPreflightCheck
{
    /// <summary>
    /// Returns <c>null</c> if all preconditions pass, or a human-readable error
    /// message string if the action should be rejected before execution.
    /// </summary>
    string? Validate(ElevationRequestDto request);
}
