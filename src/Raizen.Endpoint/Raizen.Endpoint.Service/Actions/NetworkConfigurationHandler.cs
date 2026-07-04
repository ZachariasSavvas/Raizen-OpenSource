using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Applies a static IPv4 configuration or reverts to DHCP on a network adapter.
/// Uses WMI (Win32_NetworkAdapterConfiguration) — no shell spawning.
///
/// Required parameters:
///   AdapterGuid   — NetworkInterface.Id / WMI SettingID (e.g. {12345678-...})
///   AdapterName   — Friendly name, used only for logging and result messages
///   Mode          — "Static" or "DHCP"
///
/// Additional parameters when Mode=Static:
///   IpAddress     — IPv4 address to assign
///   SubnetMask    — Subnet mask (e.g. 255.255.255.0)
///   DefaultGateway — Optional; omit or leave empty to set no gateway
/// </summary>
public sealed class NetworkConfigurationHandler(ILogger<NetworkConfigurationHandler> log)
    : IActionHandler
{
    public ActionType HandledType => ActionType.SetNetworkConfiguration;

    private static readonly Regex GuidPattern =
        new(@"^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$",
            RegexOptions.Compiled);

    public Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        var p = request.Parameters;

        // ── Validate AdapterGuid ──────────────────────────────────────────────
        if (!p.TryGetValue("AdapterGuid", out var rawGuid) || string.IsNullOrWhiteSpace(rawGuid))
            return Fail("Missing parameter: AdapterGuid");

        if (!GuidPattern.IsMatch(rawGuid))
            return Fail("AdapterGuid is not a valid GUID.");

        // Normalise to {XXXXXXXX-...} form that WMI expects
        var guid = rawGuid.StartsWith('{') ? rawGuid : $"{{{rawGuid}}}";

        var adapterName = p.GetValueOrDefault("AdapterName", guid);

        // ── Validate Mode ─────────────────────────────────────────────────────
        if (!p.TryGetValue("Mode", out var mode) || string.IsNullOrWhiteSpace(mode))
            return Fail("Missing parameter: Mode");

        mode = mode.Trim();
        if (!mode.Equals("Static", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("DHCP",   StringComparison.OrdinalIgnoreCase))
            return Fail($"Invalid Mode '{mode}'. Must be 'Static' or 'DHCP'.");

        bool isStatic = mode.Equals("Static", StringComparison.OrdinalIgnoreCase);

        string ipAddress     = "";
        string subnetMask    = "";
        string defaultGateway = "";

        if (isStatic)
        {
            if (!p.TryGetValue("IpAddress", out ipAddress!) || !IsValidIpv4(ipAddress))
                return Fail("IpAddress is missing or not a valid IPv4 address.");

            if (!p.TryGetValue("SubnetMask", out subnetMask!) || !IsValidSubnetMask(subnetMask))
                return Fail("SubnetMask is missing or not a valid subnet mask.");

            p.TryGetValue("DefaultGateway", out defaultGateway!);
            defaultGateway ??= "";

            if (!string.IsNullOrEmpty(defaultGateway) && !IsValidIpv4(defaultGateway))
                return Fail("DefaultGateway is not a valid IPv4 address.");
        }

        // ── Execute via WMI ───────────────────────────────────────────────────
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE SettingID = '{guid}'");

            ManagementObject? config = null;
            foreach (ManagementObject obj in searcher.Get())
            {
                config = obj;
                break;
            }

            if (config is null)
                return Fail($"No network adapter found with GUID {guid}.");

            if (isStatic)
            {
                // Snapshot DNS servers BEFORE EnableStatic — WMI clears them when switching
                // from DHCP to static, which would silently break name resolution.
                var existingDns = config["DNSServerSearchOrder"] as string[] ?? [];

                var enableStatic = config.GetMethodParameters("EnableStatic");
                enableStatic["IPAddress"]   = new[] { ipAddress };
                enableStatic["SubnetMask"]  = new[] { subnetMask };
                var result = (uint)(config.InvokeMethod("EnableStatic", enableStatic, null)?["ReturnValue"] ?? 1u);
                if (result != 0 && result != 1) // 0=success, 1=reboot required — both acceptable
                    return Fail($"EnableStatic returned error code {result} on adapter '{adapterName}'.");

                // Apply DNS: use custom servers if provided, otherwise restore the snapshot
                p.TryGetValue("DnsMode", out var dnsMode);
                if (dnsMode != null && dnsMode.Equals("Custom", StringComparison.OrdinalIgnoreCase))
                {
                    p.TryGetValue("PrimaryDns", out var primaryDns);
                    if (!string.IsNullOrEmpty(primaryDns) && IsValidIpv4(primaryDns))
                    {
                        var dnsServers = new List<string> { primaryDns };
                        if (p.TryGetValue("SecondaryDns", out var secondaryDns) && IsValidIpv4(secondaryDns!))
                            dnsServers.Add(secondaryDns!);

                        var dnsParams = config.GetMethodParameters("SetDNSServerSearchOrder");
                        dnsParams["DNSServerSearchOrder"] = dnsServers.ToArray();
                        config.InvokeMethod("SetDNSServerSearchOrder", dnsParams, null);
                    }
                }
                else if (existingDns.Length > 0)
                {
                    // Auto mode: restore the DNS snapshot taken before EnableStatic
                    var dnsParams = config.GetMethodParameters("SetDNSServerSearchOrder");
                    dnsParams["DNSServerSearchOrder"] = existingDns;
                    config.InvokeMethod("SetDNSServerSearchOrder", dnsParams, null);
                }

                if (!string.IsNullOrEmpty(defaultGateway))
                {
                    var setGw = config.GetMethodParameters("SetGateways");
                    setGw["DefaultIPGateway"] = new[] { defaultGateway };
                    setGw["GatewayCostMetric"] = new[] { 1 };
                    config.InvokeMethod("SetGateways", setGw, null);
                }

                string dnsSummary;
                if (dnsMode != null && dnsMode.Equals("Custom", StringComparison.OrdinalIgnoreCase))
                {
                    p.TryGetValue("PrimaryDns", out var pd);
                    p.TryGetValue("SecondaryDns", out var sd);
                    dnsSummary = "custom: " + string.Join(", ", new[] { pd, sd }.Where(s => !string.IsNullOrEmpty(s)));
                }
                else
                {
                    dnsSummary = existingDns.Length > 0 ? "auto: " + string.Join(", ", existingDns) : "none preserved";
                }
                var summary = $"{ipAddress} / {subnetMask}" +
                              (string.IsNullOrEmpty(defaultGateway) ? "" : $" (GW: {defaultGateway})") +
                              $" (DNS: {dnsSummary})";

                log.LogInformation("Request {Id}: Set static IP {Summary} on adapter '{Adapter}'.",
                    request.Id, summary, adapterName);

                return Task.FromResult(new ActionResult(true,
                    ResultMessage: $"Static IP configured on '{adapterName}': {summary}"));
            }
            else
            {
                var dhcpResult = (uint)(config.InvokeMethod("EnableDHCP", null, null)?["ReturnValue"] ?? 1u);
                if (dhcpResult != 0 && dhcpResult != 1)
                    return Fail($"EnableDHCP returned error code {dhcpResult} on adapter '{adapterName}'.");

                config.InvokeMethod("RenewDHCPLease", null, null);

                log.LogInformation("Request {Id}: Reverted adapter '{Adapter}' to DHCP.", request.Id, adapterName);

                return Task.FromResult(new ActionResult(true,
                    ResultMessage: $"Adapter '{adapterName}' reverted to DHCP."));
            }
        }
        catch (ManagementException ex)
        {
            log.LogError(ex, "Request {Id}: WMI error configuring adapter '{Adapter}'.", request.Id, adapterName);
            return Fail("Network configuration failed. See endpoint logs for details.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Request {Id}: Unhandled error configuring adapter '{Adapter}'.", request.Id, adapterName);
            return Fail("Network configuration failed. See endpoint logs for details.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsValidIpv4(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        IPAddress.TryParse(value.Trim(), out var addr) &&
        addr.AddressFamily == AddressFamily.InterNetwork;

    private static bool IsValidSubnetMask(string? value)
    {
        if (!IsValidIpv4(value)) return false;
        var bytes = IPAddress.Parse(value!.Trim()).GetAddressBytes();
        uint val  = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        uint inv  = ~val;
        return (inv & (inv + 1)) == 0;
    }

    private static Task<ActionResult> Fail(string message) =>
        Task.FromResult(new ActionResult(false, ErrorMessage: message));
}
