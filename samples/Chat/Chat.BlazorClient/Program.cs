using ChatSample.BlazorClient;
using ChatSample.BlazorClient.Chat;
using ChatSample.BlazorClient.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// The Blazor client of the GrainWire chat sample. It runs in the browser as WebAssembly. It signs in and reads
// history through the server's Web API, and gets live messages through GrainWire.Client.

WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// A browser tab has one user, so there is one of each for the whole app.
builder.Services.AddSingleton<Session>();
builder.Services.AddSingleton(new HttpClient
{
    BaseAddress = new Uri(builder.Configuration["Chat:ServerUrl"] ?? "http://localhost:5300"),
});
builder.Services.AddSingleton<ChatApi>();
builder.Services.AddSingleton<ChatConnection>();
builder.Services.AddSingleton<Inbox>();

await builder.Build().RunAsync();
