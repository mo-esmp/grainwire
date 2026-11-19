import { defineConfig } from "@playwright/test";

const port = 5210;

// `npm run e2e` builds the site into ../Chess.Server/wwwroot first, then this starts the chess server and drives it
// with real browsers.
export default defineConfig({
  testDir: "e2e",
  timeout: 90_000,
  expect: { timeout: 15_000 },
  use: { baseURL: `http://127.0.0.1:${port}` },
  webServer: {
    command: `dotnet run --project ../Chess.Server --no-launch-profile -- --urls http://127.0.0.1:${port} --Orleans:SiloPort=11511 --Orleans:GatewayPort=30511`,
    url: `http://127.0.0.1:${port}/`,
    env: { ASPNETCORE_ENVIRONMENT: "Development" },
    reuseExistingServer: false,
    timeout: 240_000,
  },
});
