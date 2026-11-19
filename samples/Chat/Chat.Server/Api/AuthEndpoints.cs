using ChatSample.Server.Auth;
using ChatSample.Server.Data;
using Microsoft.AspNetCore.Identity;

namespace ChatSample.Server.Api;

/// <summary>A username and password, sent to register or to sign in.</summary>
public sealed record Credentials(string Username, string Password);

/// <summary>The answer to a successful register or sign-in: the token to send with later requests.</summary>
public sealed record SignedIn(string Token, string Username);

/// <summary>The sign-in endpoints. They are open to everyone.</summary>
public static class AuthEndpoints
{
    private const string WrongLogin = "Wrong username or password.";

    /// <summary>Maps register and login under the given group.</summary>
    public static void MapAuth(this RouteGroupBuilder api)
    {
        RouteGroupBuilder auth = api.MapGroup("/auth");
        auth.MapPost("/register", RegisterAsync);
        auth.MapPost("/login", LoginAsync);
    }

    private static async Task<IResult> RegisterAsync(
        Credentials request, ChatStore store, TokenService tokens, PasswordHasher<string> hasher, TimeProvider time)
    {
        string? name = Rules.NormalizeUsername(request.Username);
        if (name is null)
        {
            return Results.Problem(detail: "A username has 3 to 24 letters, digits, underscores, dots or hyphens.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!Rules.IsValidPassword(request.Password))
        {
            return Results.Problem(detail: "A password needs at least 8 characters.", statusCode: StatusCodes.Status400BadRequest);
        }

        bool added = await store.AddUserAsync(name, hasher.HashPassword(name, request.Password), time.GetUtcNow());
        return added
            ? Results.Ok(new SignedIn(tokens.Issue(name), name))
            : Results.Problem(detail: "That username is taken.", statusCode: StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> LoginAsync(
        Credentials request, ChatStore store, TokenService tokens, PasswordHasher<string> hasher)
    {
        string? name = Rules.NormalizeUsername(request.Username);
        string? hash = name is null ? null : await store.GetPasswordHashAsync(name);

        // An unknown user and a wrong password get the same answer, so the API does not reveal which names exist.
        if (name is null || hash is null
            || hasher.VerifyHashedPassword(name, hash, request.Password ?? "") == PasswordVerificationResult.Failed)
        {
            return Results.Problem(detail: WrongLogin, statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(new SignedIn(tokens.Issue(name), name));
    }
}
