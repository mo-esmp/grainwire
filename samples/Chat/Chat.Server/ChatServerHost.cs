using ChatSample.Server.Api;
using ChatSample.Server.Auth;
using ChatSample.Server.Data;
using GrainWire;
using GrainWire.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChatSample.Server;

/// <summary>Builds the chat server: Orleans silo, GrainWire gateway, database, sign-in and the Web API.</summary>
public static class ChatServerHost
{
    /// <summary>
    /// Builds the app. <paramref name="configure"/> runs first, before any setting is read, so a test can change
    /// the configuration. Every setting has a default except <c>Jwt:Key</c>, which must be provided.
    /// </summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        IConfiguration config = builder.Configuration;
        JwtSettings jwt = JwtSettings.Load(config);

        builder.UseOrleans(silo =>
        {
            silo.UseLocalhostClustering(
                siloPort: config.GetValue("Orleans:SiloPort", 11112),
                gatewayPort: config.GetValue("Orleans:GatewayPort", 30001));
            silo.AddGrainWire();
        });

        // A chat client opens a room for each chat in the user's list, so it needs more than the default 10 a minute.
        builder.Services.AddGrainWireGateway(o => o.RoomCreationsPerMinute = 60);

        string database = config["Chat:Database"] ?? "Data Source=chat.db";
        builder.Services.AddDbContextFactory<ChatDb>(o => o.UseSqlite(database));
        builder.Services.AddSingleton<ChatStore>();
        builder.Services.AddSingleton(jwt);
        builder.Services.AddSingleton<TokenService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<PasswordHasher<string>>();
        builder.Services.AddSingleton<IConnectionAuthenticator, JwtConnectionAuthenticator>();

        builder.Services
            .AddAuthentication("Bearer")
            .AddJwtBearer(o =>
            {
                // Keep claim names as they are in the token, so "sub" stays "sub".
                o.MapInboundClaims = false;
                o.TokenValidationParameters = jwt.ToValidationParameters();
            });
        builder.Services.AddAuthorization();

        string[] origins = config.GetSection("Chat:ClientOrigins").Get<string[]>() ?? ["http://localhost:5302"];
        builder.Services.AddCors(o => o.AddDefaultPolicy(policy =>
            policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

        WebApplication app = builder.Build();

        using (IServiceScope scope = app.Services.CreateScope())
        {
            using ChatDb db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ChatDb>>().CreateDbContext();
            db.Database.EnsureCreated();
        }

        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGrainWire();

        app.MapGroup("/api").MapAuth();
        app.MapGroup("/api").RequireAuthorization().MapChats();

        return app;
    }
}
