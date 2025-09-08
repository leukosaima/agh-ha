# Gatus Integration Guide

This guide covers everything you need to know about integrating Gatus with AdGuard Home HA using active polling.

## Quick Start

1. **Configure endpoints in Gatus** → Set up health monitoring endpoints
2. **Configure services in AdGuard Home HA** → Map Gatus endpoints to DNS rewrites and specify Gatus instance URLs
3. **AdGuard Home HA polls Gatus API** → Continuously checks endpoint health status

## How It Works

AdGuard Home HA actively polls Gatus instances using the Gatus API:

1. **Polling**: AdGuard Home HA makes HTTP requests to `http://gatus-instance:8080/api/v1/endpoints/statuses`
2. **Status Parsing**: Gatus returns endpoint statuses in JSON format
3. **Health Calculation**: AdGuard Home HA aggregates endpoint health based on `RequiredGatusEndpoints` threshold
4. **DNS Updates**: DNS rewrites are updated when service health changes

## Configuration

### AdGuard Home HA Configuration

Configure services in `appsettings.json` with Gatus polling:

```json
{
  "AdGuardHomeHA": {
    "Services": [
      {
        "Name": "Web Service",
        "MonitoringMode": "Gatus",
        "IpAddress": "192.168.1.100",
        "Priority": 1,
        "DnsRewrites": ["web.example.com"],
        "GatusEndpointNames": ["primary-web-check", "secondary-web-check"],
        "GatusInstanceUrls": ["http://gatus-1:8080", "http://gatus-2:8080"],
        "RequiredGatusEndpoints": 1
      },
      {
        "Name": "Backup Web Service",
        "MonitoringMode": "Gatus",
        "IpAddress": "192.168.1.101",
        "Priority": 2,
        "DnsRewrites": ["web.example.com"],
        "GatusEndpointNames": ["backup-web-check"],
        "GatusInstanceUrls": ["http://gatus-1:8080"],
        "RequiredGatusEndpoints": 1
      }
    ],
    "GatusPolling": {
      "IntervalSeconds": 30,
      "TimeoutSeconds": 10
    }
  }
}
```

**Configuration Notes:**
- `RequiredGatusEndpoints`: At least 1 out of 2 endpoints must be healthy for Web Service
- `GatusInstanceUrls`: URLs of Gatus instances to poll for endpoint statuses
- `DnsRewrites`: Both services manage the same DNS entry for failover capability
- `IntervalSeconds`: How often to poll Gatus instances (default: 30 seconds)

### Gatus Configuration

**Primary Gatus Machine:**
```yaml
endpoints:
  - name: "primary-web-check"  # Must match GatusEndpointNames
    url: "http://192.168.1.100:8080/health"
    interval: 30s
    conditions:
      - "[STATUS] == 200"
      - "[RESPONSE_TIME] < 2000"
        
  - name: "backup-web-check"
    url: "http://192.168.1.101:8080/health"
    interval: 30s
    conditions:
      - "[STATUS] == 200"
      - "[RESPONSE_TIME] < 2000"
```

**Secondary Gatus Machine (for redundancy):**
```yaml
endpoints:
  - name: "secondary-web-check"  # Must match GatusEndpointNames
    url: "http://192.168.1.100:8080/health"  # Same service, different monitor
    interval: 30s
    conditions:
      - "[STATUS] == 200"
      - "[RESPONSE_TIME] < 2000"
```

## Per-Endpoint Timeout Tracking

**Key Feature**: Individual endpoints are tracked independently. If a Gatus machine crashes, only its endpoints become stale while others continue working.

### How It Works

1. **Individual Timestamps**: Each endpoint gets its own `LastSeen` timestamp
2. **Background Monitoring**: Timer checks every 60 seconds for stale endpoints  
3. **Graceful Degradation**: Services stay healthy if enough endpoints are active
4. **Automatic Recovery**: When endpoints come back, they resume normal operation

### Example Scenario: Primary Machine Crashes

| Time | Event | Endpoint Status | Service Health | Action |
|------|-------|-----------------|----------------|--------|
| 10:00 | Normal | `primary-web-check: healthy`<br>`secondary-web-check: healthy` | **Healthy** | Normal operation |
| 10:01 | **Primary crashes** | No change yet | **Healthy** | Secondary keeps sending webhooks |
| 10:04 | Stale detection | `primary-web-check: STALE`<br>`secondary-web-check: healthy` | **Healthy** (1/2 healthy, need 1) | Stale endpoint marked unhealthy |
| 10:15 | Primary recovers | `primary-web-check: healthy`<br>`secondary-web-check: healthy` | **Healthy** | Full redundancy restored |

### Benefits Over Old System

**❌ Old System (Service-Level Timeout)**:
- Primary machine crashes → Secondary keeps updating `LastUpdated`
- System never detects primary failure → DNS stays broken

**✅ New System (Per-Endpoint Timeout)**:
- Primary machine crashes → Only `primary-web-check` goes stale
- `secondary-web-check` keeps service healthy → DNS stays working
- When primary returns → Automatic recovery

## Testing Webhooks

### Manual Testing

```bash
# Test healthy webhook
curl -X POST http://localhost:8080/webhook \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer your-secure-token-here" \
  -d '{
    "endpointName": "primary-web-check",
    "success": true,
    "timestamp": "2025-01-08T12:00:00Z"
  }'

# Test unhealthy webhook
curl -X POST http://localhost:8080/webhook \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer your-secure-token-here" \
  -d '{
    "endpointName": "primary-web-check",
    "success": false,
    "timestamp": "2025-01-08T12:00:00Z"
  }'

# Test health endpoint
curl http://localhost:8080/webhook/health
```

## Health Aggregation & Failover

### How Service Health is Calculated

Services aggregate health from multiple endpoints using the `RequiredGatusEndpoints` threshold:

```json
{
  "GatusEndpointNames": ["primary-check", "secondary-check", "external-check"],
  "RequiredGatusEndpoints": 2
}
```

In this example, 2 out of 3 endpoints must be healthy for the service to be considered healthy.

**Health Scenarios:**
- ✅ **3 healthy**: Service = healthy
- ✅ **2 healthy**: Service = healthy (meets threshold)
- ❌ **1 healthy**: Service = unhealthy (below threshold)
- ❌ **0 healthy**: Service = unhealthy
- ⏰ **Stale endpoints**: Marked unhealthy after timeout (default 3 minutes)

### DNS Failover Logic

When a service becomes unhealthy:

1. **Find Alternative**: Locate next highest priority healthy service
2. **Update DNS**: Change DNS rewrites to point to alternative service IP
3. **Log Action**: Record the failover for monitoring
4. **Auto Recovery**: When original service recovers, automatically fail back

**Priority System**: Lower numbers = higher priority (Priority 1 beats Priority 2)

## Advanced Configuration

### Multiple Gatus Machines

**Use Case**: Primary and secondary Gatus machines for redundancy

```json
{
  "Services": [
    {
      "Name": "Critical Service",
      "GatusEndpointNames": [
        "primary-machine-check",
        "secondary-machine-check",
        "external-monitor-check"
      ],
      "RequiredGatusEndpoints": 2,
      "DnsRewrites": ["api.example.com"]
    }
  ]
}
```

**Endpoint Sources:**
- `primary-machine-check` - From primary Gatus installation
- `secondary-machine-check` - From secondary Gatus installation  
- `external-monitor-check` - From external monitoring service
- `RequiredGatusEndpoints: 2` - Need 2 out of 3 endpoints healthy

### Best Practices

1. **Timeout Settings**: 
   ```json
   "HealthStatusTimeoutSeconds": 180
   ```
   Set to 3x your Gatus check interval for reliable stale detection.

2. **Endpoint Naming**: Use descriptive names that identify the monitoring source:
   ```yaml
   - name: "dc1-web-health"     # Datacenter 1
   - name: "dc2-web-health"     # Datacenter 2  
   - name: "external-web-ping"  # External monitoring
   ```

3. **Redundancy Strategy**: Always have at least 2 endpoints per critical service

4. **Priority Planning**: Design priority levels for logical failover chains

## Troubleshooting

### Common Issues

**Endpoint Not Found**:
```
[ERROR] Received webhook for endpoint 'unknown-check' but no matching service configuration found
```
→ Check that endpoint name matches `GatusEndpointNames` exactly

**Service Goes Stale**:
```
[WARNING] Gatus endpoint primary-check marked as stale (last seen: 2025-01-08 10:00:45)
```
→ Primary Gatus machine may have crashed - check machine status

**Authentication Failures**:
```
[WARNING] Webhook received with invalid auth token
```
→ Verify `AuthToken` matches between Gatus and AdGuard Home HA configs

### Log Analysis

Key log messages to monitor:
- `Service health changed` - Indicates DNS failover events
- `endpoint marked as stale` - Shows silent failure detection
- `endpoint recovered from staleness` - Confirms recovery

This configuration provides true high availability with automatic failover and recovery, even when entire Gatus machines crash.
