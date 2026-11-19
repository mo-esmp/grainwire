using ChessFromScratch.BlazorClient;
using ChessFromScratch.BlazorClient.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// The Blazor client of the GrainWire chess sample. It runs in the browser as WebAssembly and talks to the chess
// server with GrainWire.Client, the same library a desktop app or a bot would use.

WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// A browser tab has one user, so there is one of each for the whole app.
builder.Services.AddSingleton<PlayerName>();
builder.Services.AddSingleton<ChessConnection>();

await builder.Build().RunAsync();
