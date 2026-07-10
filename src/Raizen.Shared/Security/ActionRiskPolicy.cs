using Raizen.Shared.Enums;

namespace Raizen.Shared.Security;

public enum ActionRiskLevel
{
    Standard = 0,
    High = 1,
}

public static class ActionRiskPolicy
{
    public static ActionRiskLevel GetRiskLevel(ActionType actionType) => actionType switch
    {
        ActionType.RunAsAdmin
            or ActionType.RunApprovedScript
            or ActionType.InstallMsi
            or ActionType.UninstallMsi
            or ActionType.DeleteFile
            or ActionType.SetRegistryValue
            or ActionType.DeleteRegistryValue
            or ActionType.SetEnvironmentVariable
            or ActionType.AddLocalGroupMember
            or ActionType.RemoveLocalGroupMember
            or ActionType.CreateLocalUser
            or ActionType.DisableLocalUser
            or ActionType.AddTrustedCertificate
            or ActionType.SetFirewallRule
            or ActionType.SetNetworkConfiguration
            or ActionType.OpenFileProperties => ActionRiskLevel.High,
        _ => ActionRiskLevel.Standard,
    };

    public static bool RequiresHumanReview(ActionType actionType) =>
        GetRiskLevel(actionType) == ActionRiskLevel.High;

    public static string Label(ActionType actionType) =>
        RequiresHumanReview(actionType) ? "High risk" : "Standard";

    public static string Recommendation(ActionType actionType) => actionType switch
    {
        ActionType.RunAsAdmin =>
            "Confirm the executable, publisher or hash, business ticket, and requester before approval.",
        ActionType.RunApprovedScript =>
            "Confirm the registered script hash, source, and change ticket before approval.",
        ActionType.InstallMsi or ActionType.UninstallMsi =>
            "Confirm the package identity, source, and rollback path before approval.",
        ActionType.AddLocalGroupMember or ActionType.RemoveLocalGroupMember or ActionType.CreateLocalUser or ActionType.DisableLocalUser =>
            "Confirm the account, group, and business owner before approval.",
        ActionType.SetRegistryValue or ActionType.DeleteRegistryValue or ActionType.SetEnvironmentVariable =>
            "Confirm the target, value, and rollback plan before approval.",
        ActionType.DeleteFile or ActionType.OpenFileProperties =>
            "Confirm the path, owner, and expected access change before approval.",
        ActionType.AddTrustedCertificate =>
            "Confirm the certificate thumbprint and issuing authority before approval.",
        ActionType.SetFirewallRule or ActionType.SetNetworkConfiguration =>
            "Confirm the expected connectivity impact before approval.",
        _ =>
            "Review the requester, endpoint, parameters, and ticket before approval.",
    };
}
