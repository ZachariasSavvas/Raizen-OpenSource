namespace Raizen.Server.Core.Services;

public sealed class ApprovalToastNotifier : IApprovalToastNotifier
{
    public event Action<Guid>? OnNewRequest;

    public void NotifyNewRequest(Guid requestId)
    {
        OnNewRequest?.Invoke(requestId);
    }
}
