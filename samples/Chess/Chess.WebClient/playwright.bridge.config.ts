import { defineConfig } from "@playwright/test";

const port = 5211;
const rulesPort = 5311;
const python = process.env.PYTHON ?? "python";

// `npm run e2e:bridge` plays the same games as `npm run e2e`, against Chess.BridgeServer with the rules in Python.
// It needs a Python with python-chess installed (see ../rules-python/README.md); set PYTHON to pick one.
export default defineConfig({
  testDir: "e2e",
  timeout: 90_000,
  expect: { timeout: 15_000 },
  use: { baseURL: `http://127.0.0.1:${port}` },
  webServer: [
    {
      command: `${python} ../rules-python/server.py --port ${rulesPort}`,
      url: `http://127.0.0.1:${rulesPort}/health`,
      reuseExistingServer: false,
      timeout: 30_000,
    },
    {
      command:
        `dotnet run --project ../Chess.BridgeServer --no-launch-profile -- --urls http://127.0.0.1:${port} ` +
        `--Orleans:SiloPort=11512 --Orleans:GatewayPort=30512 --Rules:Url=http://127.0.0.1:${rulesPort}/`,
      url: `http://127.0.0.1:${port}/`,
      env: { ASPNETCORE_ENVIRONMENT: "Development" },
      reuseExistingServer: false,
      timeout: 240_000,
    },
  ],
});
