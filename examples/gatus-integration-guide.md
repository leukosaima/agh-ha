# Gatus Integration Guide

This guide covers everything you need to know about integrating Gatus with AdGuard Home HA using active polling.

## Quick Start

1. **Configure endpoints in Gatus** → Set up health monitoring endpoints
2. **Configure services in AdGuard Home HA** → Map Gatus endpoints to DNS rewrites and specify Gatus instance URLs
3. **AdGuard Home HA polls Gatus API** → Continuously checks endpoint health status

## How It Works

AdGuard Home HA actively polls Gatus instances using the Gatus API:

1. **Polling**: AdGuard Home HA makes HTTP requests to `http://gatus-instance:8080/api/v1/endpoints/statuses`
2. **Status Parsing**: Gatus returns endpoint statuses in JSON format using keys like `"<GROUP>_<NAME>"`
3. **Key Matching**: AdGuard Home HA looks up endpoints using full Gatus keys (not just names)
4. **Health Calculation**: AdGuard Home HA aggregates endpoint health based on `RequiredGatusEndpoints` threshold
5. **DNS Updates**: DNS rewrites are updated when service health changes

### Important: Gatus Key Format

Gatus internally uses keys in the format `"<GROUP_NAME>_<ENDPOINT_NAME>"` where:
- Special characters (` `, `/`, `_`, `,`, `.`, `#`) are replaced with `-`
- **Example**: `name: "web-check"` + `group: "production"` = key `"production_web-check"`
- **Your config must use the full key**: `"GatusEndpointNames": ["production_web-check"]`

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
        "GatusEndpointNames": ["production_primary-web-check", "production_secondary-web-check"],
        "GatusInstanceUrls": ["http://gatus-1:8080", "http://gatus-2:8080"],
        "RequiredGatusEndpoints": 1
      },
      {
        "Name": "Backup Web Service",
        "MonitoringMode": "Gatus",
        "IpAddress": "192.168.1.101",
        "Priority": 2,
        "DnsRewrites": ["web.example.com"],
        "GatusEndpointNames": ["production_backup-web-check"],
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
  - name: "primary-web-check"  # Key: production_primary-web-check
    group: "production"
    url: "http://192.168.1.100:8080/health"
    interval: 30s
    conditions:
      - "[STATUS] == 200"
      - "[RESPONSE_TIME] < 2000"
        
  - name: "backup-web-check"
    group: "production"         # Key: production_backup-web-check
    url: "http://192.168.1.101:8080/health"
    interval: 30s
    conditions:
      - "[STATUS] == 200"
      - "[RESPONSE_TIME] < 2000"
```

**Secondary Gatus Machine (for redundancy):**
```yaml
endpoints:
  - name: "secondary-web-check"  # Key: production_secondary-web-check
    group: "production"
    url: "http://192.168.1.100:8080/health"  # Same service, different monitor
    interval: 30s
    conditions:
      - "[STATUS] == 200"
      - "[RESPONSE_TIME] < 2000"
```

## Per-Instance Polling

**Key Feature**: AdGuard Home HA polls multiple Gatus instances independently. If a Gatus machine goes down, only endpoints from that instance are affected.

### How It Works

1. **Independent Polling**: Each Gatus instance URL is polled separately
2. **Timeout Handling**: HTTP timeouts (10s default) mark endpoints as unreachable
3. **Graceful Degradation**: Services stay healthy if enough endpoints are reachable
4. **Automatic Recovery**: When Gatus instances come back online, endpoints resume normal status

### Example Scenario: Primary Gatus Machine Crashes

| Time | Event | Endpoint Status | Service Health | Action |
|------|-------|-----------------|----------------|--------|
| 10:00 | Normal | `primary-web-check: healthy` (gatus-1)<br>`secondary-web-check: healthy` (gatus-2) | **Healthy** | Normal operation |
| 10:01 | **Gatus-1 crashes** | Polling gatus-1 times out | **Healthy** | Gatus-2 still responding |
| 10:01 | Next poll cycle | `primary-web-check: UNREACHABLE`<br>`secondary-web-check: healthy` | **Healthy** (1/2 healthy, need 1) | DNS stays active |
| 10:15 | Gatus-1 recovers | Both instances responding | **Healthy** | Full redundancy restored |

### Benefits of Polling Approach

**✅ Active Monitoring**:
- Predictable polling intervals (30s default)
- No dependency on webhook delivery reliability
- Works regardless of Gatus alert configuration

**✅ Instance Redundancy**:
- Multiple Gatus instances provide monitoring redundancy
- Timeout on one instance doesn't affect others
- Immediate recovery when instances come back online

## Testing Gatus Integration

### Manual Testing

```bash
# Test Gatus API directly
curl -s "http://gatus-instance:8080/api/v1/endpoints/statuses" | jq

# Check specific endpoint status
curl -s "http://gatus-instance:8080/api/v1/endpoints/statuses" | jq '."production_web-check"'

# Monitor AdGuard Home HA logs for polling
docker-compose logs -f adguard-home-ha | grep "Gatus"

# Check current DNS rewrites
curl -s "http://adguard-home:3000/control/rewrite/list" \\
  -H "Authorization: Basic $(echo -n 'admin:password' | base64)"
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
- ⏰ **Unreachable Gatus**: Endpoints from unreachable Gatus instances are excluded

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
