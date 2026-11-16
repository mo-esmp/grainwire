using GrainWire;
using GrainWire.Server;

// The GrainWire chess sample: one process that is an Orleans silo, the MQTT gateway, and the web server
// for the browser client (built into wwwroot by samples/Chess/Web).

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.UseOrleans(silo =>
{
    silo.UseLocalhostClustering(
        siloPort: builder.Configuration.GetValue("Orleans:SiloPort", 11111),
        gatewayPort: builder.Configuration.GetValue("Orleans:GatewayPort", 30000));

    // Games are saved, so a game survives its grain being restarted. In-memory storage keeps the sample free of
    // infrastructure; use a real storage provider (Azure, ADO.NET, Redis ...) to survive a process restart too.
    silo.AddMemoryGrainStorage("chess");

    silo.AddGrainWire(gw => gw.ConfigureRoom("chess", room =>
    {
        room.StorageProvider = "chess";
        room.GracePeriod = TimeSpan.FromSeconds(builder.Configuration.GetValue("Chess:GraceSeconds", 60));
    }));
});

// No IConnectionAuthenticator is registered, so the development authenticator is used:
// the name a player types is trusted as their identity. Replace it before going live.
builder.Services.AddGrainWireGateway();

WebApplication app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGrainWire();
app.Run();
