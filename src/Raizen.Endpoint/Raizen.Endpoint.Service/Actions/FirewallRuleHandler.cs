using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

/// <summary>
/// Creates or updates a Windows Firewall rule using the COM API (HNetCfg.FwPolicy2).
///
/// Required parameters:
///   RuleName  — Display name of the firewall rule
///   Action    — "Allow" or "Block"
///   Direction — "Inbound" or "Outbound"
///
/// Optional parameters:
///   Protocol       — "TCP", "UDP", or "Any" (default: "Any")
///   LocalPort      — Port number or range (e.g. "1433", "8000-8100")
///   RemotePort     — Port number or range
///   RemoteAddress  — IP, CIDR, or range (e.g. "192.168.1.0/24", "*")
///   Program        — Full path to the program to allow/block
///   Enabled        — "true" or "false" (default: "true")
///
/// Security: uses COM API directly, no shell invocation (no netsh, no PowerShell).
/// </summary>
public sealed class FirewallRuleHandler(ILogger<FirewallRuleHandler> log) : IActionHandler, IPreflightCheck
{
    private static readonly Regex SafeRuleName = new(
        @"^[\w\s\.\-\(\)]{1,256}$",
        RegexOptions.None, TimeSpan.FromSeconds(1));

    private static readonly Regex PortPattern = new(
        @"^(\d{1,5}(-\d{1,5})?)$",
        RegexOptions.None, TimeSpan.FromSeconds(1));

    private static readonly Regex SafePath = new(
        @"^[A-Za-z]:\\(?!.*\.\.)[^""<>|?*\r\n]+\.(exe|dll|sys)$",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

    // NET_FW_IP_PROTOCOL constants
    private const int NET_FW_IP_PROTOCOL_TCP = 6;
    private const int NET_FW_IP_PROTOCOL_UDP = 17;
    private const int NET_FW_IP_PROTOCOL_ANY = 256;

    // NET_FW_ACTION constants
    private const int NET_FW_ACTION_BLOCK = 0;
    private const int NET_FW_ACTION_ALLOW = 1;

    // NET_FW_RULE_DIRECTION constants
    private const int NET_FW_RULE_DIR_IN = 1;
    private const int NET_FW_RULE_DIR_OUT = 2;

    public ActionType HandledType => ActionType.SetFirewallRule;

    public string? Validate(ElevationRequestDto request)
    {
        var ruleName = request.Parameters.GetValueOrDefault("RuleName", "");
        if (string.IsNullOrWhiteSpace(ruleName))
            return "RuleName parameter is required.";
        if (!SafeRuleName.IsMatch(ruleName))
            return "RuleName contains invalid characters or exceeds 256 characters.";

        var action = request.Parameters.GetValueOrDefault("Action", "");
        if (!action.Equals("Allow", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("Block", StringComparison.OrdinalIgnoreCase))
            return "Action must be 'Allow' or 'Block'.";

        var direction = request.Parameters.GetValueOrDefault("Direction", "");
        if (!direction.Equals("Inbound", StringComparison.OrdinalIgnoreCase) &&
            !direction.Equals("Outbound", StringComparison.OrdinalIgnoreCase))
            return "Direction must be 'Inbound' or 'Outbound'.";

        var protocol = request.Parameters.GetValueOrDefault("Protocol", "Any");
        if (!protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase) &&
            !protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase) &&
            !protocol.Equals("Any", StringComparison.OrdinalIgnoreCase))
            return "Protocol must be 'TCP', 'UDP', or 'Any'.";

        if (request.Parameters.TryGetValue("LocalPort", out var localPort) && !string.IsNullOrWhiteSpace(localPort))
        {
            if (!ValidatePort(localPort))
                return $"LocalPort '{localPort}' is not a valid port or range (1-65535).";
            if (protocol.Equals("Any", StringComparison.OrdinalIgnoreCase))
                return "LocalPort requires Protocol to be TCP or UDP.";
        }

        if (request.Parameters.TryGetValue("RemotePort", out var remotePort) && !string.IsNullOrWhiteSpace(remotePort))
        {
            if (!ValidatePort(remotePort))
                return $"RemotePort '{remotePort}' is not a valid port or range (1-65535).";
            if (protocol.Equals("Any", StringComparison.OrdinalIgnoreCase))
                return "RemotePort requires Protocol to be TCP or UDP.";
        }

        if (request.Parameters.TryGetValue("Program", out var program) && !string.IsNullOrWhiteSpace(program))
        {
            if (!SafePath.IsMatch(program))
                return $"Program path '{program}' is not valid.";
        }

        return null;
    }

    public Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        var p = request.Parameters;
        var ruleName = p.GetValueOrDefault("RuleName", "");
        var action = p.GetValueOrDefault("Action", "");
        var direction = p.GetValueOrDefault("Direction", "");
        var protocol = p.GetValueOrDefault("Protocol", "Any");
        var localPort = p.GetValueOrDefault("LocalPort", "");
        var remotePort = p.GetValueOrDefault("RemotePort", "");
        var remoteAddress = p.GetValueOrDefault("RemoteAddress", "*");
        var program = p.GetValueOrDefault("Program", "");
        var enabled = !string.Equals(p.GetValueOrDefault("Enabled", "true"), "false", StringComparison.OrdinalIgnoreCase);

        if (!SafeRuleName.IsMatch(ruleName))
            return Task.FromResult(new ActionResult(false, ErrorMessage: "RuleName is invalid."));

        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                ?? throw new InvalidOperationException("Windows Firewall COM component not available.");
            var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule")
                ?? throw new InvalidOperationException("Windows Firewall Rule COM component not available.");

            dynamic policy = Activator.CreateInstance(policyType)!;

            // Check for existing rule by name and remove it (update = delete + recreate)
            bool replaced = false;
            try
            {
                foreach (dynamic existingRule in policy.Rules)
                {
                    if (string.Equals((string)existingRule.Name, ruleName, StringComparison.OrdinalIgnoreCase))
                    {
                        policy.Rules.Remove(ruleName);
                        replaced = true;
                        break;
                    }
                }
            }
            catch { /* No existing rule found, or enumeration failed */ }

            dynamic rule = Activator.CreateInstance(ruleType)!;
            rule.Name = ruleName;
            rule.Description = $"Managed by Raizen (Request: {request.Id.ToString()[..8]})";
            rule.Action = action.Equals("Allow", StringComparison.OrdinalIgnoreCase)
                ? NET_FW_ACTION_ALLOW : NET_FW_ACTION_BLOCK;
            rule.Direction = direction.Equals("Inbound", StringComparison.OrdinalIgnoreCase)
                ? NET_FW_RULE_DIR_IN : NET_FW_RULE_DIR_OUT;
            rule.Enabled = enabled;

            // Protocol
            int protocolNum = protocol.ToUpperInvariant() switch
            {
                "TCP" => NET_FW_IP_PROTOCOL_TCP,
                "UDP" => NET_FW_IP_PROTOCOL_UDP,
                _ => NET_FW_IP_PROTOCOL_ANY,
            };
            rule.Protocol = protocolNum;

            // Ports (only valid for TCP/UDP)
            if (protocolNum is NET_FW_IP_PROTOCOL_TCP or NET_FW_IP_PROTOCOL_UDP)
            {
                if (!string.IsNullOrWhiteSpace(localPort))
                    rule.LocalPorts = localPort;
                if (!string.IsNullOrWhiteSpace(remotePort))
                    rule.RemotePorts = remotePort;
            }

            // Remote address
            if (!string.IsNullOrWhiteSpace(remoteAddress))
                rule.RemoteAddresses = remoteAddress;

            // Program
            if (!string.IsNullOrWhiteSpace(program))
                rule.ApplicationName = program;

            policy.Rules.Add(rule);

            var verb = replaced ? "Updated" : "Created";
            var summary = $"{verb} firewall rule '{ruleName}': {action} {direction} {protocol}";
            if (!string.IsNullOrWhiteSpace(localPort)) summary += $" port {localPort}";
            if (!string.IsNullOrWhiteSpace(remoteAddress) && remoteAddress != "*")
                summary += $" from {remoteAddress}";

            log.LogInformation("[Request:{RequestId}] {Summary}", request.Id, summary);
            return Task.FromResult(new ActionResult(true, ResultMessage: summary));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Request:{RequestId}] FirewallRule failed.", request.Id);
            return Task.FromResult(new ActionResult(false, ErrorMessage: "Failed to set firewall rule. See endpoint logs for details."));
        }
    }

    private static bool ValidatePort(string port)
    {
        if (!PortPattern.IsMatch(port)) return false;
        var parts = port.Split('-');
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var num) || num < 1 || num > 65535)
                return false;
        }
        if (parts.Length == 2 && int.Parse(parts[0]) >= int.Parse(parts[1]))
            return false;
        return true;
    }
}
