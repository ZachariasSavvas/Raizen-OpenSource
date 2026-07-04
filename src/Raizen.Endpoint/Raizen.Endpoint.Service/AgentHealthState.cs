namespace Raizen.Endpoint.Service;

public sealed class AgentHealthState
{
    private readonly object _lock = new();
    private Snapshot _snapshot = new();

    public Snapshot Current
    {
        get { lock (_lock) return _snapshot; }
    }

    public void PollSucceeded()
    {
        lock (_lock)
        {
            _snapshot = _snapshot with
            {
                LastPollSucceededAt = DateTimeOffset.UtcNow,
                LastPollError = null,
            };
        }
    }

    public void PollFailed(string error)
    {
        lock (_lock)
        {
            _snapshot = _snapshot with { LastPollError = Trim(error) };
        }
    }

    public void UpdateChecked(string status, string? error = null)
    {
        lock (_lock)
        {
            _snapshot = _snapshot with
            {
                LastUpdateCheckAt = DateTimeOffset.UtcNow,
                LastUpdateStatus = Trim(status, 64),
                LastUpdateError = Trim(error),
            };
        }
    }

    public void UpdateLaunched()
    {
        lock (_lock)
        {
            _snapshot = _snapshot with
            {
                LastUpdateCheckAt = DateTimeOffset.UtcNow,
                LastUpdateStatus = "install-started",
                LastUpdateError = null,
            };
        }
    }

    public void MarkCurrentVersionHealthy()
    {
        lock (_lock)
        {
            _snapshot = _snapshot with
            {
                LastSuccessfulUpdateAt = DateTimeOffset.UtcNow,
                LastUpdateStatus = string.IsNullOrEmpty(_snapshot.LastUpdateStatus)
                    ? "healthy"
                    : _snapshot.LastUpdateStatus,
            };
        }
    }

    private static string? Trim(string? value, int max = 1000)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= max ? value : value[..max];
    }

    public sealed record Snapshot
    {
        public DateTimeOffset? LastPollSucceededAt { get; init; }
        public string? LastPollError { get; init; }
        public DateTimeOffset? LastUpdateCheckAt { get; init; }
        public string? LastUpdateStatus { get; init; }
        public string? LastUpdateError { get; init; }
        public DateTimeOffset? LastSuccessfulUpdateAt { get; init; }
    }
}
