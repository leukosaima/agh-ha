# AdGuard Home High Availability (HA)

AdGuard Home HA is a containerized .NET background service that keeps AdGuard Home DNS rewrites pointed at the highest-priority healthy target.

It monitors a set of services (each service owns one IP address plus the DNS rewrites it should serve) and automatically fails over when health changes.

## Features
- Automatic failover for AdGuard Home DNS rewrites
- Per-domain, priority-based target selection (lowest `Priority` wins)
- Health checks via:
  - `Ping` (ICMP)
  - `Gatus` (polling the Gatus API)
- Runs as a Docker container (no ports exposed)

## Prerequisites
- Docker + Docker Compose v2 (`docker compose`)
- An AdGuard Home instance (API reachable from the container)

## Quick start (Docker)

### 1) Copy templates
```bash
cp .env.example .env
cp config/appsettings.production.json.example config/appsettings.production.json
```

### 2) Configure AdGuard Home credentials
Edit `.env`:
```bash
ADGUARD_BASE_URL=http://your-adguard-home:3000
ADGUARD_USERNAME=admin
ADGUARD_PASSWORD=your-secure-password
```

### 3) Configure services + DNS rewrites
Edit `config/appsettings.production.json`. Minimal example:
```json
{
  "AdGuardHomeHA": {
    "AdGuardHome": {
      "BaseUrl": "http://your-adguard-home:3000",
      "Username": "admin",
      "Password": "your-secure-password"
    },
    "Services": [
      {
        "Name": "Primary",
        "MonitoringMode": "Ping",
        "IpAddress": "192.168.1.100",
        "Priority": 1,
        "TimeoutMs": 5000,
        "DnsRewrites": ["service.example.com"]
      },
      {
        "Name": "Backup",
        "MonitoringMode": "Ping",
        "IpAddress": "192.168.1.101",
        "Priority": 2,
        "TimeoutMs": 5000,
        "DnsRewrites": ["service.example.com"]
      }
    ]
  }
}
```

### 4) Start
```bash
docker compose up -d --build
```
(If you use the legacy plugin, `docker-compose up -d --build` also works.)

### 5) Logs / status
```bash
docker compose logs -f adguard-home-ha
docker compose ps
```

## Monitoring modes

### Ping
Ping checks are executed by calling the `ping` binary with Linux flags. For ping monitoring, run the app in the provided Linux container (recommended) or another Linux environment (e.g. WSL).

### Gatus (API polling)
For advanced health checks (HTTP/TCP/etc.), configure a service with `MonitoringMode: "Gatus"` plus:
- `GatusEndpointNames` (full Gatus keys: `<group>_<name>`)
- `GatusInstanceUrls` (one or more Gatus base URLs)
- `RequiredGatusEndpoints` (threshold)

See `examples/gatus-integration-guide.md` for a full walkthrough.

## Troubleshooting
- `Configuration validation failed`: check that `AdGuardHomeHA:Services` is not empty and each service has `Name`, `IpAddress`, `Priority`, and `DnsRewrites`.
- Cannot connect/authenticate to AdGuard Home: verify `ADGUARD_BASE_URL` and credentials; confirm the API is reachable from inside the container.
- Ping services always unhealthy: ensure ICMP is allowed from the container host to your targets.
- Gatus endpoints not found: make sure `GatusEndpointNames` uses the endpoint key format (`<group>_<name>`), not just the display name.

## References
- AdGuard Home: https://github.com/AdguardTeam/AdGuardHome
- Gatus: https://github.com/TwinProduction/gatus

## Contributing
PRs welcome.

## License
MIT


