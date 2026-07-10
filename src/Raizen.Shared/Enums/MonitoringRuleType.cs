namespace Raizen.Shared.Enums;

public enum MonitoringRuleType
{
    EndpointOfflineMinutes = 1,
    LowSystemDriveFreePercent = 2,
    HighMemoryUsedPercent = 3,
    DefenderDisabled = 4,
    DefenderSignatureAgeDays = 5,
    BitLockerNotProtected = 6,
    PendingReboot = 7,
    HealthCollectionFailed = 8,
    ServiceStopped = 9,
}

public enum MonitoringSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}
