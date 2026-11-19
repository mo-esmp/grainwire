using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChessBridge.Server.Bridge;

/// <summary>
/// Calls the external rules service over HTTP. The base address and timeout are set where it is registered (Program.cs).
/// </summary>
public sealed class RulesClient(HttpClient http)
{
    /// <summary>
    /// Sends one room operation to the rules service and returns its decision.
    /// </summary>
    /// <exception cref="RulesServiceException">The service answered with an error or an empty body.</exception>
    /// <exception cref="HttpRequestException">The service could not be reached.</exception>
    /// <exception cref="TaskCanceledException">The service did not answer in time.</exception>
    public async Task<RulesDecision> DecideAsync(RulesRequest request)
    {
        // A sized body, not PostAsJsonAsync's chunked stream: simple HTTP servers (Python's http.server among them)
        // only read bodies that have a Content-Length.
        using ByteArrayContent content = new(JsonSerializer.SerializeToUtf8Bytes(request, RulesJson.Options));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using HttpResponseMessage response = await http.PostAsync("decide", content);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new RulesServiceException($"The rules service answered {(int)response.StatusCode}: {body}");
        }

        return await response.Content.ReadFromJsonAsync<RulesDecision>(RulesJson.Options)
            ?? throw new RulesServiceException("The rules service answered with an empty body.");
    }
}

/// <summary>
/// The rules service failed. The room operation is undone and the player gets an error.
/// </summary>
public sealed class RulesServiceException(string message) : Exception(message);
