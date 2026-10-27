using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Skua.Mcp;

/// <summary>
/// Thin HTTP client for the Skua.App.Avalonia control API. Configuration is
/// intentionally environment-only so tokens never appear in a process list.
/// </summary>
public sealed class ControlClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public ControlClient()
    {
        _baseUrl = (ReadEnv("WAYFER_CONTROL_URL", "SKUA_CONTROL_URL") ?? "http://127.0.0.1:8766").Trim().TrimEnd('/');
        if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out Uri? baseUri))
            throw new InvalidOperationException($"Invalid WAYFER_CONTROL_URL: {_baseUrl}");

        string token = (ReadEnv("WAYFER_CONTROL_TOKEN", "SKUA_CONTROL_TOKEN") ?? string.Empty).Trim();

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(15)
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token.Length > 0)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public string BaseUrl => _baseUrl;

    public Task<string> GetAsync(string path, CancellationToken ct)
    {
        return SendAsync(() => _http.GetAsync(path, ct), ct);
    }

    public Task<string> PostAsync<T>(string path, T body, CancellationToken ct)
    {
        return SendAsync(() => _http.PostAsJsonAsync(path, body, JsonOptions, ct), ct);
    }

    private async Task<string> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await send().ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return body;

            return JsonSerializer.Serialize(new
            {
                ok = false,
                status = (int)response.StatusCode,
                error = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body
            }, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Error("request_cancelled", "The MCP request was cancelled.");
        }
        catch (TaskCanceledException)
        {
            return Error("control_timeout", $"Wayfer control API at {_baseUrl} did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            return Error(
                "control_unreachable",
                $"Cannot reach Wayfer control API at {_baseUrl}. Is Wayfer running with the control server enabled? ({ex.Message})");
        }
        catch (Exception ex)
        {
            return Error("control_error", ex.Message);
        }
    }

    private static string? ReadEnv(string primary, string legacy)
    {
        string? value = Environment.GetEnvironmentVariable(primary);
        return string.IsNullOrEmpty(value)
            ? Environment.GetEnvironmentVariable(legacy)
            : value;
    }

    private static string Error(string code, string message)
    {
        return JsonSerializer.Serialize(new { ok = false, error = new { code, message } }, JsonOptions);
    }
}
