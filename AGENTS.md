# AGENTS.md

Guidance for any AI coding agent working in this repository.

## What this repo is
AdGuard Home HA is a .NET background service (no HTTP endpoints) that monitors service health and updates AdGuard Home DNS rewrites.

It is typically run as a Linux container.

## Project layout
- `Program.cs`: host/DI wiring + hosted service registration
- `Services/`
  - `AdGuardHomeHaService.cs`: main orchestrator
  - `ServiceHealthMonitor.cs`: ICMP ping checks (Linux `ping -c ...`)
  - `GatusPollingHealthMonitor.cs`: polls the Gatus API for endpoint status
  - `DnsRewriteManager.cs`: applies per-domain rewrite updates
  - `AdGuardHomeClient.cs`: AdGuard Home API client (cookie/session auth)
- `Models/`: configuration models (bound from the `AdGuardHomeHA` section)
- `config/appsettings.production.json.example`: canonical config example
- `docker-compose.yml`, `Dockerfile`: container build/run

## Run locally (recommended)
```bash
cp .env.example .env
cp config/appsettings.production.json.example config/appsettings.production.json
docker compose up -d --build
docker compose logs -f adguard-home-ha
```

## Notes that matter for changes
- Ping monitoring is implemented by shelling out to `ping` with Linux flags; don’t assume it works on Windows without WSL.
- If you change configuration models in `Models/`, update:
  - `config/appsettings.production.json.example`
  - `README.md` (keep it concise)
- Avoid documenting commands/flags that don’t exist in code.

## Suggested validation
```bash
dotnet build -c Release
```
