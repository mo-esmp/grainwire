using GrainWire;
using GrainWire.Server;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.UseOrleans(silo =>
{
    silo.UseLocalhostClustering(
        siloPort: builder.Configuration.GetValue("Orleans:SiloPort", 11111),
        gatewayPort: builder.Configuration.GetValue("Orleans:GatewayPort", 30000));

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

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGrainWire();

app.Run();
