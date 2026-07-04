namespace Raizen.Server.Core.Services;

/// <summary>
/// In-process event bus for real-time approval toast notifications.
/// The PgNotifyListenerService fires events; Blazor ToastContainer subscribes.
/// </summary>
public interface IApprovalToastNotifier
{
    event Action<Guid>? OnNewRequest;
    void NotifyNewRequest(Guid requestId);
}
