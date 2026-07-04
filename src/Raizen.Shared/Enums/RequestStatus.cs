namespace Raizen.Shared.Enums;

public enum RequestStatus
{
    /// <summary>Request submitted, awaiting approver review.</summary>
    Pending = 0,

    /// <summary>Request approved, queued for execution on the endpoint.</summary>
    Approved = 1,

    /// <summary>Request denied by an approver.</summary>
    Denied = 2,

    /// <summary>Endpoint picked up and is executing the action.</summary>
    Executing = 3,

    /// <summary>Action completed successfully on the endpoint.</summary>
    Succeeded = 4,

    /// <summary>Action failed during execution on the endpoint.</summary>
    Failed = 5,

    /// <summary>Request cancelled by the requester before approval.</summary>
    Cancelled = 6,

    /// <summary>Request expired without being acted on.</summary>
    Expired = 7
}
