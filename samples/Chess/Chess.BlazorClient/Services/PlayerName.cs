using Microsoft.JSInterop;

namespace ChessFromScratch.BlazorClient.Services;

/// <summary>
/// The name the player goes by. It is kept in the browser's local storage, so it is still there after a reload.
/// The chess server trusts this name as the player's identity (GrainWire's development authenticator).
/// </summary>
public sealed class PlayerName(IJSRuntime js)
{
    private const string Key = "chess.name";
    private const string Letters = "abcdefghijklmnopqrstuvwxyz0123456789";

    private string? _name;

    /// <summary>
    /// Returns the stored name. A first-time visitor gets a made-up one such as <c>guest-k3f9</c>.
    /// </summary>
    public async ValueTask<string> GetAsync()
    {
        if (_name is not null)
        {
            return _name;
        }

        string? stored = await js.InvokeAsync<string?>("localStorage.getItem", Key);
        if (string.IsNullOrWhiteSpace(stored))
        {
            stored = "guest-" + string.Concat(Enumerable.Range(0, 4).Select(_ => Letters[Random.Shared.Next(Letters.Length)]));
            await js.InvokeVoidAsync("localStorage.setItem", Key, stored);
        }

        _name = stored;
        return stored;
    }

    /// <summary>
    /// Stores a new name. An empty name is ignored.
    /// </summary>
    public async ValueTask SetAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return;
        }

        _name = name;
        await js.InvokeVoidAsync("localStorage.setItem", Key, name);
    }
}
