using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Creates, updates, appends to, removes an entry from, or deletes a Windows
/// system environment variable (HKLM\...\Environment).
///
/// Required parameters:
///   VariableName  — name of the environment variable
///   Operation     — Set | Append | Prepend | RemoveEntry | Delete
///   Value         — new value or entry (not required for Delete)
///
/// PATH is handled safely: Append/Prepend skip duplicate entries (case-insensitive);
/// Deleting PATH itself is blocked to prevent system breakage.
///
/// After any change WM_SETTINGCHANGE is broadcast so running applications
/// pick up the new value without a reboot.
/// </summary>
public sealed class EnvironmentVariableHandler(ILogger<EnvironmentVariableHandler> log)
    : IActionHandler
{
    public ActionType HandledType => ActionType.SetEnvironmentVariable;

    private const string EnvKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    private static readonly Regex NamePattern =
        new(@"^[A-Za-z_][A-Za-z0-9_()\{\}\.\-]*$", RegexOptions.Compiled);

    private static readonly HashSet<string> BlockedDeletes =
        new(StringComparer.OrdinalIgnoreCase) { "PATH" };

    public Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        var p = request.Parameters;

        // ── Validate VariableName ─────────────────────────────────────────────
        if (!p.TryGetValue("VariableName", out var name) || string.IsNullOrWhiteSpace(name))
            return Fail("Missing parameter: VariableName");

        name = name.Trim();
        if (name.Length > 255 || !NamePattern.IsMatch(name))
            return Fail($"VariableName '{name}' contains invalid characters.");

        // ── Validate Operation ────────────────────────────────────────────────
        if (!p.TryGetValue("Operation", out var op) || string.IsNullOrWhiteSpace(op))
            return Fail("Missing parameter: Operation");

        op = op.Trim();
        if (!new[] { "Set", "Append", "Prepend", "RemoveEntry", "Delete" }
                .Contains(op, StringComparer.OrdinalIgnoreCase))
            return Fail($"Invalid Operation '{op}'. Must be Set | Append | Prepend | RemoveEntry | Delete.");

        if (op.Equals("Delete", StringComparison.OrdinalIgnoreCase) &&
            BlockedDeletes.Contains(name))
            return Fail($"Deleting the '{name}' variable is not permitted.");

        // ── Validate Value ────────────────────────────────────────────────────
        p.TryGetValue("Value", out var value);
        value = value?.Trim() ?? "";

        bool needsValue = !op.Equals("Delete", StringComparison.OrdinalIgnoreCase);
        if (needsValue && string.IsNullOrEmpty(value))
            return Fail($"Parameter 'Value' is required for operation '{op}'.");

        if (value.Length > 2048)
            return Fail("Value exceeds the 2048-character limit.");

        // ── Apply ─────────────────────────────────────────────────────────────
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(EnvKeyPath, writable: true)
                ?? throw new InvalidOperationException("Cannot open environment registry key.");

            string result;

            if (op.Equals("Set", StringComparison.OrdinalIgnoreCase))
            {
                var kind = name.Equals("PATH", StringComparison.OrdinalIgnoreCase)
                    ? RegistryValueKind.ExpandString
                    : RegistryValueKind.String;
                key.SetValue(name, value, kind);
                result = $"Set '{name}' = '{Truncate(value)}'.";
            }
            else if (op.Equals("Append", StringComparison.OrdinalIgnoreCase) ||
                     op.Equals("Prepend", StringComparison.OrdinalIgnoreCase))
            {
                var existing = ReadRaw(key, name);
                var entries  = SplitEntries(existing);

                if (entries.Any(e => e.Equals(value, StringComparison.OrdinalIgnoreCase)))
                {
                    result = $"Entry '{Truncate(value)}' already exists in '{name}' — no change made.";
                }
                else
                {
                    if (op.Equals("Append", StringComparison.OrdinalIgnoreCase))
                        entries.Add(value);
                    else
                        entries.Insert(0, value);

                    key.SetValue(name, string.Join(";", entries), RegistryValueKind.ExpandString);
                    result = $"{op}ed '{Truncate(value)}' to '{name}'.";
                }
            }
            else if (op.Equals("RemoveEntry", StringComparison.OrdinalIgnoreCase))
            {
                var existing = ReadRaw(key, name);
                var entries  = SplitEntries(existing);
                int before   = entries.Count;
                entries.RemoveAll(e => e.Equals(value, StringComparison.OrdinalIgnoreCase));

                if (entries.Count == before)
                    return Fail($"Entry '{Truncate(value)}' was not found in '{name}'.");

                key.SetValue(name, string.Join(";", entries), RegistryValueKind.ExpandString);
                result = $"Removed '{Truncate(value)}' from '{name}'.";
            }
            else // Delete
            {
                key.DeleteValue(name, throwOnMissingValue: false);
                result = $"Deleted variable '{name}'.";
            }

            BroadcastEnvironmentChange();

            log.LogInformation("Request {Id}: Environment variable change — {Result}",
                request.Id, result);

            return Task.FromResult(new ActionResult(true, ResultMessage: result));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Request {Id}: Error modifying environment variable '{Name}'.",
                request.Id, name);
            return Fail("Environment variable operation failed. See endpoint logs for details.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ReadRaw(RegistryKey key, string name) =>
        key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";

    private static List<string> SplitEntries(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries)
             .Select(e => e.Trim())
             .Where(e => !string.IsNullOrEmpty(e))
             .ToList();

    private static string Truncate(string s) =>
        s.Length > 80 ? s[..77] + "..." : s;

    private static void BroadcastEnvironmentChange()
    {
        try
        {
            SendMessageTimeout(
                (IntPtr)0xFFFF,   // HWND_BROADCAST
                0x001A,           // WM_SETTINGCHANGE
                UIntPtr.Zero,
                "Environment",
                0x0002,           // SMTO_ABORTIFHUNG
                5000,
                out _);
        }
        catch { /* non-critical — change is already applied in registry */ }
    }

    private static Task<ActionResult> Fail(string message) =>
        Task.FromResult(new ActionResult(false, ErrorMessage: message));

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
}
