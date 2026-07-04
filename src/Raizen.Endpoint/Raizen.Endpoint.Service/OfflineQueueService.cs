using System.Text.Json;
using Microsoft.Extensions.Logging;
using Raizen.Endpoint.Service.Actions;

namespace Raizen.Endpoint.Service;

/// <summary>
/// Persists execution results that could not be reported to the server
/// (e.g. because connectivity dropped) so they can be replayed on the next
/// poll cycle when the server is reachable again.
///
/// File: %ProgramData%\Raizen\pending-results.json
/// </summary>
public sealed class OfflineQueueService(ILogger<OfflineQueueService> log)
{
    private static readonly string QueuePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Raizen", "pending-results.json");

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly object _lock = new();

    public sealed record PendingResult(
        Guid      RequestId,
        bool      Succeeded,
        string?   ResultMessage,
        string?   ErrorMessage,
        DateTimeOffset ExecutedAt);

    public void Enqueue(Guid requestId, ActionResult result)
    {
        lock (_lock)
        {
            var items = ReadItems();
            items.Add(new PendingResult(
                requestId,
                result.Succeeded,
                result.ResultMessage,
                result.ErrorMessage,
                DateTimeOffset.UtcNow));
            WriteItems(items);
        }

        log.LogInformation("Queued offline result for request {RequestId} (succeeded={Ok}).",
            requestId, result.Succeeded);
    }

    public List<PendingResult> DequeueAll()
    {
        lock (_lock)
        {
            var items = ReadItems();
            if (items.Count > 0)
            {
                try { File.Delete(QueuePath); }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Could not delete offline queue file; items may be replayed next cycle.");
                }
            }
            return items;
        }
    }

    public bool HasItems()
    {
        try { return File.Exists(QueuePath); }
        catch { return false; }
    }

    private List<PendingResult> ReadItems()
    {
        try
        {
            if (!File.Exists(QueuePath)) return [];
            var json = File.ReadAllText(QueuePath);
            return JsonSerializer.Deserialize<List<PendingResult>>(json, JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read offline queue file; treating as empty.");
            return [];
        }
    }

    private void WriteItems(List<PendingResult> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(QueuePath)!);
            File.WriteAllText(QueuePath, JsonSerializer.Serialize(items, JsonOpts));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not write offline queue file.");
        }
    }
}
