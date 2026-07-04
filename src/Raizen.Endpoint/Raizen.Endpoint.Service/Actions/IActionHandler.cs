using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Contract for a single, atomic elevation action.
///
/// SECURITY RULES for all implementations:
///   ✗ Must NOT spawn cmd.exe, powershell.exe, wscript, cscript, or any shell
///   ✗ Must NOT create a process with an elevated token passed to external programs
///   ✗ Must NOT grant a user-visible "admin session" (no Explorer elevation)
///   ✓ Must use .NET / Win32 APIs directly
///   ✓ Must log every action and its outcome
///   ✓ Must be idempotent where possible
///   ✓ Must validate all parameter values independently (defense-in-depth)
/// </summary>
public interface IActionHandler
{
    ActionType HandledType { get; }

    Task<ActionResult> ExecuteAsync(
        ElevationRequestDto request,
        CancellationToken ct);
}

public sealed record ActionResult(
    bool Succeeded,
    string? ResultMessage = null,
    string? ErrorMessage = null);
