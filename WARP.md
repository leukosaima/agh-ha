# WARP.md

This file provides guidance to WARP (warp.dev) when working with code in this repository.

## Repository Overview

This is a .NET 9 application that provides high availability for AdGuard Home DNS rewrites by monitoring services via ping or Gatus API polling and automatically managing DNS entries. The application runs as a Docker container and integrates directly with AdGuard Home's REST API.

**Key Features:**
- **Service-based monitoring**: Each service manages its own DNS rewrites
- **Dual monitoring modes**: Ping-based or Gatus polling per service
- **IP deduplication**: Multiple services on the same IP are pinged only once
- **Per-service failover**: Services can failover to other healthy services based on priority

## Development Commands

### Build and Run
```bash
# Build and run with Docker Compose (recommended)
docker-compose up -d

# Build standalone container
docker build -t adguard-home-ha .

# Run for development (requires configuration)
dotnet run

# Build for release
dotnet build -c Release
```

### Testing and Development
```bash
# Check application logs
docker-compose logs -f adguard-home-ha

# Check container health
docker inspect --format='{{.State.Health.Status}}' adguard-home-ha

# View health check logs
docker inspect --format='{{range .State.Health.Log}}{{.Output}}{{end}}' adguard-home-ha

# Stop and remove containers
docker-compose down
```

### Configuration Setup
```bash
# Copy configuration templates
cp .env.example .env
cp config/appsettings.production.json.example config/appsettings.production.json

# Edit configuration files (required before first run)
# Edit .env for basic AdGuard Home connection
# Edit config/appsettings.production.json for services and Gatus polling configuration
```

### Configuration Testing
```bash
# Test configuration with dry run
dotnet run --dry-run

# Check application logs for Gatus polling
docker-compose logs -f adguard-home-ha | grep "Gatus"
```

## Architecture

### Core Services Architecture
The application follows a service-oriented architecture with four main services orchestrated by a hosted service:

- **AdGuardHomeHaService** (Services/AdGuardHomeHaService.cs): Main orchestrator that runs monitoring loops and coordinates between services
- **ServiceHealthMonitor** (Services/ServiceHealthMonitor.cs): Monitors service availability via ping with IP deduplication and configurable retry logic
- **GatusPollingHealthMonitor** (Services/GatusPollingHealthMonitor.cs): Polls Gatus API endpoints for health status
- **DnsRewriteManager** (Services/DnsRewriteManager.cs): Manages AdGuard Home DNS rewrites per service through API calls
- **AdGuardHomeClient** (Services/AdGuardHomeClient.cs): HTTP client wrapper for AdGuard Home REST API with authentication handling

### Configuration Model
All configuration is split across individual model files with hierarchical settings:
- **AppConfiguration**: Root configuration container
- **AdGuardHomeConfiguration**: API connection settings
- **ServiceConfiguration[]**: Monitored services with individual DNS rewrites and monitoring modes
- **MonitoringConfiguration**: Health check intervals and retry settings
- **GatusPollingConfiguration**: Gatus polling settings and timeouts

### Runtime State Models
- **ServiceHealthStatus**: Tracks service health with `GatusEndpoints` (endpoint health) and `EndpointLastSeen` (per-endpoint timestamps)
- **GatusEndpointStatus**: DTO for Gatus API response structure

### Failover Logic
The application implements service-based failover with per-service DNS management:
1. Each service monitors its own health (ping or Gatus polling)
2. Each service manages its own DNS rewrites independently
3. On service failure, its DNS rewrites failover to the next highest priority healthy service
4. IP deduplication ensures efficient ping monitoring for services sharing IP addresses
5. Gatus polling services aggregate health from multiple Gatus endpoints based on configured thresholds

### Authentication Flow
AdGuardHomeClient handles session-based authentication:
- Performs login to AdGuard Home API on initialization
- Manages session cookies for subsequent API requests
- Retries authentication on session expiry
- Provides detailed logging for authentication debugging

### Gatus Integration
The application polls Gatus API endpoints for health status monitoring:

**Core Features:**
- Configurable polling intervals for Gatus instances
- Aggregates health status from multiple Gatus endpoints per service
- Supports threshold-based health determination (e.g., 2 out of 3 endpoints must be healthy)
- Automatic failover when polled health status changes

**Multi-Instance Polling:**
- **Redundant Monitoring**: Can poll multiple Gatus instances for the same endpoints
- **HTTP Timeout Handling**: Configurable timeouts for API requests
- **Endpoint Aggregation**: Service health calculated from all available endpoint statuses
- **Automatic Recovery**: Services automatically recover when endpoints return to healthy state

## Key Configuration Points

### Service Priority System
Lower priority numbers = higher preference for failover. When a service fails, its DNS rewrites failover to the lowest priority number among healthy services.

### Per-Service DNS Management
Each service manages its own DNS rewrites independently. When a service becomes unhealthy, only its DNS rewrites are updated to point to a healthy alternative service.

### Dual Monitoring Modes
- **Ping mode**: Traditional ping-based monitoring with IP deduplication
- **Gatus polling mode**: Actively polls Gatus API endpoints for health status

### Monitoring Behavior

**Ping-Based Services:**
- Main service runs a monitoring loop with configurable intervals (default 30 seconds)
- IP deduplication: Services sharing IPs are pinged once per cycle  
- Uses Linux ping command with timeouts (requires --cap-add=NET_RAW for containers)
- Failed health checks trigger immediate DNS updates for affected services

**Gatus Polling Services:**
- Independent polling of Gatus API endpoints (default 30s interval)
- Per-endpoint health tracking across multiple Gatus instances
- Configurable polling intervals and HTTP timeouts
- Service health aggregated based on RequiredGatusEndpoints threshold
- DNS updates triggered immediately via ServiceHealthChanged events
- **No main monitoring loop needed** - runs purely on events

**Mixed Mode:**
- Ping services use the main monitoring loop
- Gatus services run independently via events
- Both can coexist and failover to each other based on priority

**Gatus-Only Mode:**
- No main monitoring loop runs
- All monitoring handled by Gatus polling events
- More efficient for pure Gatus deployments

## Development Notes

### Dependency Injection Setup
Program.cs configures services for background operation:
- Uses Host.CreateApplicationBuilder for console application
- HTTP clients are registered for AdGuardHomeClient and GatusPollingHealthMonitor
- All services are singletons due to background service nature
- Configuration is bound from appsettings sections

### Error Handling Strategy
- Extensive logging at appropriate levels (Debug/Info/Warning/Error/Critical)
- Service failures are logged but don't crash the application
- Authentication retries are handled gracefully
- Ping failures are retried with configurable attempts

### Docker Considerations
- Uses multi-stage build (SDK for build, .NET runtime for final image)
- Runs as non-root user for security
- Includes health checks for container orchestration
- Requires NET_RAW capability for ping functionality
- No ports exposed - pure background service

### Gatus Polling Implementation

**HTTP Client Configuration:**
- `GatusPollingHealthMonitor` uses HttpClient for API requests
- Configurable timeout per `GatusPollingConfiguration.TimeoutSeconds`
- Thread-safe concurrent health status tracking

**Polling Implementation:**
- Background `Timer` polls Gatus instances at configurable intervals
- Polls `/api/v1/endpoints/statuses` API endpoint on each Gatus instance
- Parses JSON response to extract endpoint health status
- Thread safety ensured with `SemaphoreSlim` during status updates

**Health Aggregation Logic:**
- Health status aggregation uses `RequiredGatusEndpoints` threshold per service
- Service health events fired when overall service health state changes
- `GatusEndpointNames` must use full Gatus keys: `"<GROUP_NAME>_<ENDPOINT_NAME>"`
- Special characters in group/endpoint names are replaced with `-` by Gatus
- Multiple `GatusInstanceUrls` provide redundancy for endpoint monitoring
- Example: Gatus endpoint `name: "web"` + `group: "prod"` = key `"prod_web"`

### Environment Variable Override
Configuration can be overridden via environment variables using ASP.NET Core's hierarchical configuration system (e.g., `AdGuardHomeHA__AdGuardHome__BaseUrl`).

## Examples and Documentation

### Documentation Structure
- **README.md**: Getting started guide focused on ping monitoring with clear progression to Gatus polling
- **WARP.md**: This file - comprehensive development and architecture reference
- **examples/gatus-integration-guide.md**: Complete Gatus integration guide with polling configuration
- **examples/gatus-config.yaml**: Comprehensive Gatus configuration with best practices

### Key Documentation Principles
- **Layered Approach**: Basic (README) → Advanced (Gatus polling guide)
- **No Content Duplication**: Each file serves a distinct purpose
- **Clear Cross-References**: Files reference each other appropriately
- **Up-to-Date Examples**: All examples use current "endpoint" terminology (not "instances")

### Testing Integration
For comprehensive Gatus integration scenarios, see `examples/gatus-integration-guide.md` which includes:
- Configuration examples for Gatus polling
- Multi-instance Gatus deployment patterns
- Troubleshooting polling connectivity issues
