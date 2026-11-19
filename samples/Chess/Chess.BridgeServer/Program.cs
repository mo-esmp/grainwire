using ChessBridge.Server.Bridge;
using GrainWire;
using GrainWire.Server;
using Microsoft.Extensions.FileProviders;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// The rules service. A slow or failed call undoes the room operation and the player gets an error,
// so keep the timeout well below the clients' request timeout (15 seconds by default).
builder.Services.AddHttpClient<RulesClient>(http =>
{
    http.BaseAddress = new Uri(builder.Configuration.GetValue("Rules:Url", "http://localhost:5310/")!);
    http.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Rules:TimeoutSeconds", 3));
});

builder.UseOrleans(silo =>
{
    // Different ports from Chess.Server, so both samples can run at the same time.
    silo.UseLocalhostClustering(
        siloPort: builder.Configuration.GetValue("Orleans:SiloPort", 11112),
        gatewayPort: builder.Configuration.GetValue("Orleans:GatewayPort", 30001));

    const string storageName = "chess";
    silo.AddMemoryGrainStorage(storageName);

    silo.AddGrainWire(gw => gw.ConfigureRoom("chess", room =>
    {
        room.StorageProvider = storageName;
        room.GracePeriod = TimeSpan.FromSeconds(builder.Configuration.GetValue("Chess:GraceSeconds", 60));
    }));
});
builder.Services.AddGrainWireGateway();

WebApplication app = builder.Build();

// Serve the browser client that Chess.WebClient builds into Chess.Server/wwwroot: the same client plays against either server.
string webRoot = Path.GetFullPath(Path.Combine(
    app.Environment.ContentRootPath, builder.Configuration.GetValue("Chess:WebRoot", "../Chess.Server/wwwroot")!));
if (Directory.Exists(webRoot))
{
    PhysicalFileProvider files = new(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}
else
{
    app.Logger.LogWarning("No browser client at {WebRoot}. Build Chess.WebClient first (see the README).", webRoot);
}

app.MapGrainWire();

app.Run();
