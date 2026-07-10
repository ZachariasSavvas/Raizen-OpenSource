using System.Diagnostics.Eventing.Reader;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Raizen.Endpoint.Shared.Config;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Endpoint.Service.Actions;

public sealed class CollectEventLogsHandler(
    ConfigLoader configLoader,
    IHttpClientFactory httpFactory,
    EndpointHealthCollector healthCollector,
    ILogger<CollectEventLogsHandler> log) : IActionHandler, IPreflightCheck
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Dictionary<string, string> AllowedChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System"] = "System",
        ["Application"] = "Application",
        ["Defender"] = "Microsoft-Windows-Windows Defender/Operational",
        ["WindowsUpdate"] = "Microsoft-Windows-WindowsUpdateClient/Operational",
    };

    public ActionType HandledType => ActionType.CollectEventLogs;

    public string? Validate(ElevationRequestDto request)
    {
        try { _ = ParseOptions(request.Parameters); return null; }
        catch (ArgumentException ex) { return ex.Message; }
    }

    public async Task<ActionResult> ExecuteAsync(ElevationRequestDto request, CancellationToken ct)
    {
        DiagnosticOptions options;
        try { options = ParseOptions(request.Parameters); }
        catch (ArgumentException ex) { return new(false, ErrorMessage: ex.Message); }

        try
        {
            var events = await Task.Run(() => CollectEvents(options, ct), ct);
            var content = CreateBundle(request, options, events);
            if (content.Length > DiagnosticBundleServiceLimits.MaxBundleBytes)
                return new(false, ErrorMessage: "Diagnostic bundle exceeded the 10 MB limit.");

            var safeMachine = new string(Environment.MachineName
                .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            var fileName = $"Raizen-Diagnostics-{safeMachine}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
            var upload = new DiagnosticBundleUploadDto
            {
                RequestId = request.Id,
                FileName = fileName,
                ContentBase64 = Convert.ToBase64String(content),
                Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                EventCount = events.Count,
                GeneratedAt = DateTimeOffset.UtcNow,
            };

            using var client = BuildClient();
            var response = await client.PostAsJsonAsync("api/v1/diagnostics", upload, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                return new(false, ErrorMessage: $"Diagnostic upload failed ({response.StatusCode}): {Trim(error, 500)}");
            }

            var saved = await response.Content.ReadFromJsonAsync<DiagnosticBundleUploadResponseDto>(ct);
            log.LogInformation("Uploaded diagnostic bundle {BundleId} for request {RequestId}", saved?.Id, request.Id);
            return new(true, $"Collected {events.Count} events. Bundle {saved?.Id} is available for 7 days.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "Diagnostic collection failed for request {RequestId}", request.Id);
            return new(false, ErrorMessage: $"Diagnostic collection failed: {Trim(ex.Message, 500)}");
        }
    }

    internal static DiagnosticOptions ParseOptions(IReadOnlyDictionary<string, string> parameters)
    {
        var channelsValue = parameters.GetValueOrDefault("Channels") ?? "System,Application";
        var channels = channelsValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (channels.Count == 0 || channels.Count > 4 || channels.Any(x => !AllowedChannels.ContainsKey(x)))
            throw new ArgumentException("Channels must contain only System, Application, Defender, or WindowsUpdate.");

        if (!int.TryParse(parameters.GetValueOrDefault("Hours") ?? "24", out var hours) || hours is < 1 or > 72)
            throw new ArgumentException("Hours must be between 1 and 72.");
        if (!int.TryParse(parameters.GetValueOrDefault("MaxEvents") ?? "500", out var maxEvents) || maxEvents is < 1 or > 1000)
            throw new ArgumentException("MaxEvents must be between 1 and 1000.");
        if (!bool.TryParse(parameters.GetValueOrDefault("IncludeInformation") ?? "false", out var includeInformation))
            throw new ArgumentException("IncludeInformation must be true or false.");

        return new(channels.Select(x => AllowedChannels[x]).ToList(), hours, maxEvents, includeInformation);
    }

    private List<CollectedEvent> CollectEvents(DiagnosticOptions options, CancellationToken ct)
    {
        var output = new List<CollectedEvent>();
        var millis = (long)TimeSpan.FromHours(options.Hours).TotalMilliseconds;
        var levels = options.IncludeInformation
            ? "Level=1 or Level=2 or Level=3 or Level=4"
            : "Level=1 or Level=2 or Level=3";
        var queryText = $"*[System[TimeCreated[timediff(@SystemTime) <= {millis}] and ({levels})]]";

        foreach (var channel in options.Channels)
        {
            if (output.Count >= options.MaxEvents) break;
            ct.ThrowIfCancellationRequested();
            try
            {
                var query = new EventLogQuery(channel, PathType.LogName, queryText) { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                while (output.Count < options.MaxEvents)
                {
                    ct.ThrowIfCancellationRequested();
                    using var record = reader.ReadEvent();
                    if (record is null) break;
                    string message;
                    try { message = record.FormatDescription() ?? ""; }
                    catch { message = "Description unavailable."; }
                    output.Add(new CollectedEvent(
                        record.TimeCreated,
                        channel,
                        record.ProviderName ?? "unknown",
                        record.Id,
                        record.LevelDisplayName ?? record.Level?.ToString() ?? "unknown",
                        Redact(Trim(message, 4000))));
                }
            }
            catch (EventLogNotFoundException)
            {
                log.LogWarning("Event log channel {Channel} is not available", channel);
            }
        }
        return output;
    }

    private byte[] CreateBundle(ElevationRequestDto request, DiagnosticOptions options, List<CollectedEvent> events)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJson(archive, "manifest.json", new
            {
                requestId = request.Id,
                machine = Environment.MachineName,
                generatedAt = DateTimeOffset.UtcNow,
                options.Hours,
                options.MaxEvents,
                options.IncludeInformation,
                channels = options.Channels,
                eventCount = events.Count,
                health = healthCollector.Collect(),
            });
            WriteJson(archive, "events.json", events);
        }
        return stream.ToArray();
    }

    private static void WriteJson(ZipArchive archive, string name, object value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, JsonOpts);
    }

    private HttpClient BuildClient()
    {
        var config = configLoader.Current;
        var client = httpFactory.CreateClient("Raizen");
        client.BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("X-Raizen-MachineId", config.MachineId);
        client.DefaultRequestHeaders.Add("X-Raizen-ApiKey", config.ApiKey);
        client.Timeout = TimeSpan.FromMinutes(2);
        return client;
    }

    private static string Redact(string value)
    {
        string[] labels = ["password=", "apikey=", "api_key=", "token=", "authorization:"];
        foreach (var label in labels)
        {
            var start = 0;
            while ((start = value.IndexOf(label, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var valueStart = start + label.Length;
                var end = value.IndexOfAny([' ', '\r', '\n', ';', ','], valueStart);
                if (end < 0) end = value.Length;
                value = value[..valueStart] + "[REDACTED]" + value[end..];
                start = valueStart + 10;
            }
        }
        return value;
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];

    internal sealed record DiagnosticOptions(List<string> Channels, int Hours, int MaxEvents, bool IncludeInformation);
    private sealed record CollectedEvent(DateTime? TimeCreated, string Channel, string Provider, int EventId, string Level, string Message);

    private static class DiagnosticBundleServiceLimits
    {
        public const int MaxBundleBytes = 10 * 1024 * 1024;
    }
}
