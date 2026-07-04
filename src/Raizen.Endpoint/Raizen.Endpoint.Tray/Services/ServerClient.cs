using System.Net.Http.Json;
using System.Text.Json;
using Raizen.Endpoint.Shared.Config;
using Raizen.Shared.DTOs;

namespace Raizen.Endpoint.Tray.Services;

/// <summary>
/// Thin HTTP wrapper used by the tray app to communicate with the Raizen server.
/// Runs as the current user; authentication is done via the endpoint API key
/// (stored in raizen-config.json) so the tray can submit requests on behalf of
/// the logged-in user.
/// </summary>
public sealed class ServerClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly ConfigLoader _configLoader;
    private volatile HttpClient _http;

    public ServerClient(ConfigLoader configLoader)
    {
        _configLoader = configLoader;
        _http = BuildClient();
        _configLoader.ConfigChanged += _ =>
        {
            var old = _http;
            _http = BuildClient();
            // Delay disposal so in-flight requests on the old client can complete
            Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
            {
                try { old.Dispose(); } catch { /* best-effort cleanup */ }
            });
        };
    }

    /// <summary>Fetches the list of enabled action definitions for display in the request form.</summary>
    public async Task<List<ActionDefinitionDto>> GetActionsAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("api/v1/actions", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<ActionDefinitionDto>>(JsonOpts, ct) ?? [];
    }

    /// <summary>Submits an elevation request. Includes the current user's UPN in a reserved parameter.</summary>
    public async Task<ElevationRequestDto> SubmitRequestAsync(
        SubmitElevationRequestDto dto,
        string requesterUpn,
        string requesterDisplayName,
        CancellationToken ct = default)
    {
        // Set requester identity in dedicated fields (preferred by servers v1.0.19+).
        // Also inject legacy parameter keys so older server versions can still attribute the request.
        dto.RequesterUpn         = requesterUpn;
        dto.RequesterDisplayName = requesterDisplayName;
        dto.Parameters["__requester_upn"]          = requesterUpn;
        dto.Parameters["__requester_display_name"] = requesterDisplayName;

        var resp = await _http.PostAsJsonAsync("api/v1/requests", dto, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ElevationRequestDto>(JsonOpts, ct)
               ?? throw new InvalidOperationException("Empty response from server.");
    }

    /// <summary>Gets the current status of a previously submitted request.</summary>
    public async Task<ElevationRequestDto?> GetRequestStatusAsync(Guid requestId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"api/v1/requests/{requestId}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ElevationRequestDto>(JsonOpts, ct);
    }

    /// <summary>Fetches all requests submitted from this machine.</summary>
    public async Task<PagedResult<ElevationRequestDto>> GetMyRequestsAsync(
        int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"api/v1/requests/mine?page={page}&pageSize={pageSize}", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<PagedResult<ElevationRequestDto>>(JsonOpts, ct)
               ?? new PagedResult<ElevationRequestDto>();
    }

    /// <summary>Returns comments for a request submitted from this machine.</summary>
    public async Task<List<RequestCommentDto>> GetCommentsAsync(Guid requestId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"api/v1/requests/{requestId}/comments", ct);
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<RequestCommentDto>>(JsonOpts, ct) ?? [];
    }

    /// <summary>Posts a comment on a request submitted from this machine.</summary>
    public async Task<RequestCommentDto?> AddCommentAsync(Guid requestId, string body, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"api/v1/requests/{requestId}/comments", new AddCommentDto { Body = body }, JsonOpts, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<RequestCommentDto>(JsonOpts, ct);
    }

    /// <summary>Quick reachability probe — returns true if the server responds.</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync("api/v1/actions", ct);
            return resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.Unauthorized;
        }
        catch
        {
            return false;
        }
    }

    private HttpClient BuildClient()
    {
        var config = _configLoader.Current;
        var handler = new HttpClientHandler();

        if (!string.IsNullOrEmpty(config.TlsPinThumbprint))
        {
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert?.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)
                    ?.Equals(config.TlsPinThumbprint, StringComparison.OrdinalIgnoreCase) == true;
        }

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(config.HttpTimeoutSeconds),
        };
        client.DefaultRequestHeaders.Add("X-Raizen-MachineId", config.MachineId);
        client.DefaultRequestHeaders.Add("X-Raizen-ApiKey", config.ApiKey);
        return client;
    }

    public void Dispose() => _http.Dispose();
}
