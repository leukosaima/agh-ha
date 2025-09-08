# WARP.md

This file provides guidance to WARP (warp.dev) when working with code in this repository.

## Repository Overview

This is a .NET 9 application that provides high availability for AdGuard Home DNS rewrites by monitoring services via ping or Gatus webhooks and automatically managing DNS entries. The application runs as a Docker container and integrates directly with AdGuard Home's REST API.

**Key Features:**
- **Service-based monitoring**: Each service manages its own DNS rewrites
- **Dual monitoring modes**: Ping-based or webhook-based (via Gatus) per service
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
# Edit config/appsettings.production.json for services and webhook configuration
```

### Webhook Testing
```bash
# Test webhook endpoint (if webhooks enabled)
curl -X POST http://localhost:8080/webhook \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer your-webhook-token" \
  -d '{"endpointName":"test-service","success":true}'

# Check webhook health endpoint
curl http://localhost:8080/webhook/health
```

## Architecture

### Core Services Architecture
The application follows a service-oriented architecture with four main services orchestrated by a hosted service:

- **AdGuardHomeHaService** (Services/AdGuardHomeHaService.cs): Main orchestrator that runs monitoring loops and coordinates between services
- **ServiceHealthMonitor** (Services/ServiceHealthMonitor.cs): Monitors service availability via ping with IP deduplication and configurable retry logic
- **WebhookHealthAggregator** (Services/WebhookHealthAggregator.cs): Aggregates health status from multiple Gatus endpoints per service
- **DnsRewriteManager** (Services/DnsRewriteManager.cs): Manages AdGuard Home DNS rewrites per service through API calls
- **AdGuardHomeClient** (Services/AdGuardHomeClient.cs): HTTP client wrapper for AdGuard Home REST API with authentication handling
- **WebhookController** (Controllers/WebhookController.cs): ASP.NET Core controller that receives Gatus webhook notifications

### Configuration Model
All configuration is split across individual model files with hierarchical settings:
- **AppConfiguration**: Root configuration container
- **AdGuardHomeConfiguration**: API connection settings
- **ServiceConfiguration[]**: Monitored services with individual DNS rewrites and monitoring modes
- **MonitoringConfiguration**: Health check intervals and retry settings
- **WebhookConfiguration**: Webhook server settings and authentication

### Runtime State Models
- **ServiceHealthStatus**: Tracks service health with `GatusEndpoints` (endpoint health) and `EndpointLastSeen` (per-endpoint timestamps)
- **GatusWebhookPayload**: Webhook payload structure received from Gatus
- **ConditionResult**: Individual health check condition results

### Failover Logic
The application implements service-based failover with per-service DNS management:
1. Each service monitors its own health (ping or webhook)
2. Each service manages its own DNS rewrites independently
3. On service failure, its DNS rewrites failover to the next highest priority healthy service
4. IP deduplication ensures efficient ping monitoring for services sharing IP addresses
5. Webhook services aggregate health from multiple Gatus endpoints based on configured thresholds

### Authentication Flow
AdGuardHomeClient handles session-based authentication:
- Performs login to AdGuard Home API on initialization
- Manages session cookies for subsequent API requests
- Retries authentication on session expiry
- Provides detailed logging for authentication debugging

### Webhook Integration
The application can receive health status updates from Gatus endpoints with advanced per-endpoint timeout tracking:

**Core Features:**
- Configurable webhook endpoint with optional Bearer token authentication
- Aggregates health status from multiple Gatus endpoints per service
- Supports threshold-based health determination (e.g., 2 out of 3 endpoints must be healthy)
- Automatic failover when webhook health status changes

**Per-Endpoint Timeout Tracking (Key Innovation):**
- **Individual Timestamps**: Each endpoint tracks its own `LastSeen` timestamp in `ServiceHealthStatus.EndpointLastSeen`
- **Silent Failure Detection**: Detects when individual Gatus machines crash without sending failure notifications
- **Background Monitoring**: Timer runs every 60 seconds to check for stale endpoints
- **Graceful Degradation**: Services remain healthy if sufficient endpoints are active
- **Independent Staleness**: Only endpoints that haven't reported recently are marked stale
- **Automatic Recovery**: When stale endpoints resume reporting, they automatically rejoin the health calculation

## Key Configuration Points

### Service Priority System
Lower priority numbers = higher preference for failover. When a service fails, its DNS rewrites failover to the lowest priority number among healthy services.

### Per-Service DNS Management
Each service manages its own DNS rewrites independently. When a service becomes unhealthy, only its DNS rewrites are updated to point to a healthy alternative service.

### Dual Monitoring Modes
- **Ping mode**: Traditional ping-based monitoring with IP deduplication
- **Webhook mode**: Receives health updates from Gatus endpoints with configurable aggregation rules

### Monitoring Behavior

**Ping-Based Services:**
- Health checks run on configurable intervals (default 30 seconds)
- IP deduplication: Services sharing IPs are pinged once per cycle  
- Uses Linux ping command with timeouts (requires --cap-add=NET_RAW for containers)
- Failed health checks trigger immediate DNS updates for affected services

**Webhook-Based Services:**
- Real-time health updates from Gatus via HTTP webhooks
- Per-endpoint timestamp tracking prevents silent failure scenarios
- Background timer (60s interval) checks for stale endpoints
- Stale endpoints are marked unhealthy after `HealthStatusTimeoutSeconds` (default 300s)
- Service health recalculated when any endpoint status changes
- DNS updates triggered immediately on service health state changes

## Development Notes

### Dependency Injection Setup
Program.cs configures services dynamically based on webhook configuration:
- Uses WebApplicationBuilder when webhooks are enabled for ASP.NET Core support
- Falls back to Host.CreateApplicationBuilder for ping-only mode
- HTTP clients are registered for AdGuardHomeClient
- Webhook services are conditionally registered
- All services are singletons due to background service nature
- Configuration is bound from appsettings sections

### Error Handling Strategy
- Extensive logging at appropriate levels (Debug/Info/Warning/Error/Critical)
- Service failures are logged but don't crash the application
- Authentication retries are handled gracefully
- Ping failures are retried with configurable attempts

### Docker Considerations
- Uses multi-stage build (SDK for build, runtime for final image)
- Runs as non-root user for security
- Includes health checks for container orchestration
- Requires NET_RAW capability for ping functionality
- Exposes port 8080 for webhook endpoints when enabled

### Webhook Development Notes

**Authentication & Security:**
- Webhook endpoint supports optional Bearer token authentication via `WebhookConfiguration.AuthToken`
- WebhookController validates authorization header before processing payloads
- Health endpoint at `/webhook/health` provides server monitoring without authentication

**Per-Endpoint Timeout Implementation:**
- `WebhookHealthAggregator` maintains `ConcurrentDictionary<string, ServiceHealthStatus>`
- Each `ServiceHealthStatus` contains `EndpointLastSeen` dictionary for individual endpoint timestamps
- `CheckAndUpdateStaleEndpoints()` method compares endpoint timestamps against timeout threshold
- Background `Timer` runs `CheckStaleEndpointsCallback()` every 60 seconds
- Thread safety ensured with `SemaphoreSlim` during health status updates

**Health Aggregation Logic:**
- `CalculateServiceHealth()` calls `CheckAndUpdateStaleEndpoints()` before aggregating
- Health status aggregation uses `RequiredGatusEndpoints` threshold per service
- Stale endpoints automatically marked as unhealthy in health calculation
- Service health events fired when overall service health state changes
- Gatus endpoint names must exactly match `ServiceConfiguration.GatusEndpointNames`

### Environment Variable Override
Configuration can be overridden via environment variables using ASP.NET Core's hierarchical configuration system (e.g., `AdGuardHomeHA__AdGuardHome__BaseUrl`).

## Examples and Documentation

### Documentation Structure
- **README.md**: Getting started guide focused on ping monitoring with clear progression to webhook monitoring
- **WARP.md**: This file - comprehensive development and architecture reference
- **examples/webhook-integration-guide.md**: Complete webhook integration guide with per-endpoint timeout tracking
- **examples/gatus-config.yaml**: Comprehensive Gatus configuration with best practices

### Key Documentation Principles
- **Layered Approach**: Basic (README) → Advanced (webhook guide)
- **No Content Duplication**: Each file serves a distinct purpose
- **Clear Cross-References**: Files reference each other appropriately
- **Up-to-Date Examples**: All examples use current "endpoint" terminology (not "instances")

### Testing Integration
For comprehensive webhook testing scenarios, see `examples/webhook-integration-guide.md` which includes:
- Manual webhook testing with curl commands
- Silent failure scenarios and detection
- Multi-machine Gatus deployment patterns
- Troubleshooting common issues
