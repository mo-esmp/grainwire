using ChatSample.BlazorClient.Chat;
using Microsoft.JSInterop;

namespace ChatSample.BlazorClient.Services;

/// <summary>
/// Who is signed in. The token and the username are kept in the browser's local storage, so a reload keeps the
/// user signed in.
/// </summary>
public sealed class Session(IJSRuntime js)
{
    private const string TokenKey = "chat.token";
    private const string UsernameKey = "chat.username";

    /// <summary>
    /// Called when the user signs in or out, or when the saved sign-in has been read. It can be called on any thread.
    /// </summary>
    public event Action? Changed;

    /// <summary>The signed-in user's name, or null.</summary>
    public string? Username { get; private set; }

    /// <summary>The token to send to the server, or null.</summary>
    public string? Token { get; private set; }

    /// <summary>True when there is a token and a username.</summary>
    public bool IsSignedIn => Token is not null && Username is not null;

    /// <summary>Reads the saved sign-in from local storage. Call it once when the app starts.</summary>
    public async ValueTask LoadAsync()
    {
        string? token = await js.InvokeAsync<string?>("localStorage.getItem", TokenKey);
        string? username = await js.InvokeAsync<string?>("localStorage.getItem", UsernameKey);
        bool both = token is not null && username is not null;
        Token = both ? token : null;
        Username = both ? username : null;
        Changed?.Invoke();
    }

    /// <summary>Remembers a new sign-in.</summary>
    public async ValueTask SignInAsync(SignedIn signedIn)
    {
        Token = signedIn.Token;
        Username = signedIn.Username;
        await js.InvokeVoidAsync("localStorage.setItem", TokenKey, signedIn.Token);
        await js.InvokeVoidAsync("localStorage.setItem", UsernameKey, signedIn.Username);
        Changed?.Invoke();
    }

    /// <summary>Forgets the sign-in, here and in local storage.</summary>
    public async ValueTask SignOutAsync()
    {
        Token = null;
        Username = null;
        await js.InvokeVoidAsync("localStorage.removeItem", TokenKey);
        await js.InvokeVoidAsync("localStorage.removeItem", UsernameKey);
        Changed?.Invoke();
    }
}
