# AdGuard Home High Availability (HA)

A .NET 9 Docker container application that provides high availability for AdGuard Home DNS rewrites by monitoring multiple machines and automatically switching DNS entries to available targets.

## Features

### Core Capabilities
- **Automatic Failover**: Monitors services and switches DNS rewrites to healthy targets
- **Priority-Based Selection**: Configure service priorities for preferred failover order  
- **REST API Integration**: Direct integration with AdGuard Home's REST API
- **Docker Support**: Containerized application for easy deployment
- **Structured Logging**: Comprehensive logging for monitoring and troubleshooting

### Monitoring Modes
- **Ping Monitoring**: Traditional ICMP ping-based health checking (simple setup)
- **Gatus Polling**: Advanced Gatus integration with API polling for comprehensive health checks
- **Configurable Health Checks**: Customizable intervals, retry attempts, and timeouts

## How It Works

1. **Service Monitoring**: Monitors configured services via ping or Gatus API polling to check availability
2. **DNS Management**: Automatically updates AdGuard Home DNS rewrites based on service health
3. **Failover Logic**: When a service becomes unavailable, switches DNS to the next healthy service with highest priority
4. **Automatic Recovery**: When a higher-priority service recovers, automatically switches back

## Prerequisites

- Docker and Docker Compose
- Access to an AdGuard Home instance
- Network connectivity to monitor target machines

## Quick Start

### 1. Clone and Configure

```bash
git clone https://github.com/leukosaima/agh-ha.git
cd agh-ha

# Copy and customize configuration files
cp .env.example .env
cp config/appsettings.production.json.example config/appsettings.production.json
```

### 2. Edit Configuration

Edit the `.env` file:

```bash
# AdGuard Home Configuration
ADGUARD_BASE_URL=http://your-adguard-home:3000
ADGUARD_USERNAME=admin
ADGUARD_PASSWORD=your-secure-password
```

Edit `config/appsettings.production.json` for basic ping monitoring:

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
        "Name": "Primary Server",
        "MonitoringMode": "Ping",
        "IpAddress": "192.168.1.100",
        "Priority": 1,
        "TimeoutMs": 5000,
        "DnsRewrites": [
          "www.yourdomain.com",
          "api.yourdomain.com"
        ]
      },
      {
        "Name": "Backup Server",
        "MonitoringMode": "Ping",
        "IpAddress": "192.168.1.101",
        "Priority": 2,
        "DnsRewrites": [
          "www.yourdomain.com",
          "api.yourdomain.com"
        ]
      }
    ]
  }
}
```

> **For Gatus polling integration**: See `examples/webhook-integration-guide.md`

### 3. Deploy

```bash
# Build and start
docker-compose up -d

# Check logs
docker-compose logs -f adguard-home-ha

# Check health
docker-compose ps
```

## Configuration Reference

### Service Configuration

| Property | Description | Default |
|----------|-------------|---------|
| `Name` | Friendly name for the service | Required |
| `MonitoringMode` | "Ping" or "Gatus" | Required |
| `IpAddress` | IP address to monitor and target | Required |
| `Priority` | Lower number = higher priority | Required |
| `TimeoutMs` | Ping timeout in milliseconds (ping mode) | 5000 |
| `DnsRewrites` | Array of domains to manage | Required |

### Monitoring Configuration

| Property | Description | Default |
|----------|-------------|---------|
| `CheckIntervalSeconds` | Seconds between health checks | 30 |
| `RetryAttempts` | Number of ping retries per check | 3 |
| `RetryDelayMs` | Delay between retry attempts | 1000 |

### AdGuard Home Configuration

| Property | Description |
|----------|-------------|
| `BaseUrl` | AdGuard Home API base URL |
| `Username` | Admin username |
| `Password` | Admin password |

## DNS Rewrites

The application manages DNS rewrites in AdGuard Home for the configured domains. When a machine becomes unavailable:

1. All configured DNS rewrites are updated to point to the next available machine
2. The application prioritizes machines by their `Priority` value (lower = higher priority)
3. Changes are logged and can be monitored

## Logging

The application provides structured logging with different levels:

- **Debug**: Detailed operation information
- **Information**: General operation status
- **Warning**: Non-critical issues (e.g., single machine down)  
- **Error**: Operation failures
- **Critical**: Service-affecting issues (e.g., no healthy machines)

## Docker Deployment Options

### Option 1: Docker Compose (Recommended)

```bash
docker-compose up -d
```

### Option 2: Standalone Docker

```bash
# Build
docker build -t adguard-home-ha .

# Run with configuration
docker run -d \\
  --name adguard-home-ha \\
  --restart unless-stopped \\
  -v $(pwd)/config/appsettings.production.json:/app/appsettings.Production.json:ro \\
  -e DOTNET_ENVIRONMENT=Production \\
  adguard-home-ha
```

### Option 3: Environment Variables Only

```bash
docker run -d \\
  --name adguard-home-ha \\
  --restart unless-stopped \\
  -e DOTNET_ENVIRONMENT=Production \\
  -e AdGuardHomeHA__AdGuardHome__BaseUrl=http://adguard-home:3000 \\
  -e AdGuardHomeHA__AdGuardHome__Username=admin \\
  -e AdGuardHomeHA__AdGuardHome__Password=yourpassword \\
  adguard-home-ha
```

## Advanced Monitoring

### Gatus API Integration

For advanced monitoring beyond basic ping checks, AdGuard Home HA integrates with [Gatus](https://github.com/TwinProduction/gatus) via API polling:

- **Active polling** - Continuously polls Gatus instances for endpoint health status
- **Redundant monitoring** - Multiple Gatus instances provide monitoring redundancy  
- **Complex health checks** - HTTP, TCP, DNS, and custom condition support
- **Reliable detection** - No dependency on webhook delivery or state changes

📖 **Full Setup Guide**: [`examples/webhook-integration-guide.md`](examples/webhook-integration-guide.md)

## Health Monitoring

The container includes health checks that monitor the application process:

```bash
# Check container health
docker inspect --format='{{.State.Health.Status}}' adguard-home-ha

# View health check logs  
docker inspect --format='{{range .State.Health.Log}}{{.Output}}{{end}}' adguard-home-ha
```

## Troubleshooting

### Common Issues

**Connection Failed to AdGuard Home**
- Verify the BaseUrl is accessible from the container
- Check username/password credentials
- Ensure AdGuard Home API is enabled

**Machines Not Responding to Ping**
- Verify IP addresses are correct
- Check network connectivity from container
- Adjust timeout values if needed

**DNS Rewrites Not Updating**  
- Check AdGuard Home API credentials
- Verify the domains exist or can be created
- Review application logs for API errors

### Debugging

Enable debug logging by setting the log level:

```json
{
  "Logging": {
    "LogLevel": {
      "AdGuardHomeHA": "Debug"
    }
  }
}
```

Or via environment variable:
```bash
-e Logging__LogLevel__AdGuardHomeHA=Debug
```

## Architecture

```
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   AdGuard HA    │────│   AdGuard Home   │────│   DNS Clients   │
│   Container     │    │                  │    │                 │
└─────────────────┘    └──────────────────┘    └─────────────────┘
         │
         ├── Ping ──────► Machine 1 (Priority 1)
         │
         └── Ping ──────► Machine 2 (Priority 2)
```

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make changes and add tests
4. Submit a pull request

## License

This project is licensed under the MIT License.

## Support

For issues and questions:
1. Check the troubleshooting section
2. Review application logs
3. Create an issue with logs and configuration details
