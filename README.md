# Custom Lightweight API Gateway

> **Enterprise Reverse Proxy & Traffic Controller**
> **Lead Engineer:** Abi Binu

## Overview

The Custom Lightweight API Gateway is a high-performance, centralized network entry point built on **.NET 10** and **YARP (Yet Another Reverse Proxy)**. It sits between client interfaces (Android/Web) and internal backend microservices, providing a single unified Base URL while offloading all cross-cutting infrastructure concerns away from individual services.

This gateway prevents internal microservices from being exposed directly to the public internet and eliminates the need to replicate security, rate-limiting, and observability logic across every backend codebase.

---

## Core Capabilities

| Feature | Description |
|---|---|
| **Dynamic Request Routing** | Routes HTTP traffic to downstream microservice clusters based on path matching, configured entirely in `appsettings.json` — no recompile needed |
| **JWT Authentication Offloading** | Validates `Bearer` tokens at the edge. Invalid or missing tokens receive a `401 Unauthorized` JSON response before ever reaching internal services |
| **Fixed Window Rate Limiting** | Protects downstream APIs from DDoS and brute-force attacks. Max 100 requests / 60 seconds per client IP, returning `429 Too Many Requests` |
| **Distributed Tracing** | Generates or forwards an `X-Correlation-ID` header on every request, enabling end-to-end tracing across all microservices in a distributed log aggregator |
| **Standardized JSON Error Handling** | Two-layer error handling: a global exception handler for connection failures and a response middleware for `502`/`503`/`504` status codes — all normalized to a consistent JSON shape |
| **Structured Logging** | Serilog-powered request logs capturing method, path, status code, elapsed time, and correlation ID. Writes to both console and a daily rolling log file |
| **Health Check Endpoint** | `GET /health` reports gateway liveness with JSON output, ready for Kubernetes probes and load balancer health checks |
| **Load Balancing** | Round-Robin distribution across cluster nodes configured per-route in `appsettings.json` |

---

## Technology Stack

| Layer | Technology |
|---|---|
| Framework | .NET 10 Web API |
| Language | C# 14 |
| Proxy Engine | YARP (`Yarp.ReverseProxy` v2.x) |
| Security | `Microsoft.AspNetCore.Authentication.JwtBearer` |
| Rate Limiting | `System.Threading.RateLimiting` (built-in) |
| Logging | `Serilog.AspNetCore` + `Serilog.Sinks.File` |

---

## System Architecture

```
Client (Android / Web)
        │
        ▼  HTTPS / HTTP
┌──────────────────────────────────────────────────┐
│              API GATEWAY  (.NET 10)              │
│                                                  │
│  ┌─ Middleware Pipeline (in order) ───────────┐  │
│  │  1.  Rate Limiter      (Fixed Window/IP)   │  │
│  │  2.  GatewayErrorMiddleware (502/503/504)  │  │
│  │  3.  CorrelationIdMiddleware               │  │
│  │  4.  RequestLoggingMiddleware              │  │
│  │  5.  Authentication    (JWT Bearer)        │  │
│  │  6.  Authorization     (named policies)   │  │
│  │  7.  YARP Reverse Proxy                   │  │
│  └────────────────────────────────────────────┘  │
│                                                  │
│  Endpoints                                       │
│    GET /health   → liveness probe (public)       │
└────────────────────────┬─────────────────────────┘
                         │  Internal HTTP
           ┌─────────────┼─────────────┐
           ▼             ▼             ▼
      [Auth API]    [Users API]  [Inventory API]
```

---

## Project Structure

```
api-gateway/
├── ApiGateway/                         # The gateway application
│   ├── Middleware/
│   │   ├── CorrelationIdMiddleware.cs  # Injects X-Correlation-ID
│   │   ├── GatewayErrorMiddleware.cs  # Normalizes 502/503/504 to JSON
│   │   └── RequestLoggingMiddleware.cs # Structured request/response logs
│   ├── logs/                           # Daily rolling log files (gitignored)
│   ├── appsettings.json                # All routes, clusters, JWT, Serilog config
│   └── Program.cs                      # Service registration + pipeline wiring
│
├── MockService/                        # Fake downstream microservice (dev only)
│   └── Program.cs                      # Auth, Users, Inventory + /dev/token
│
└── ApiGateway.slnx                     # Solution file (.NET 10 XML format)
```

---

## Request Lifecycle

Every incoming request walks the pipeline in this exact order:

| Step | Stage | Action |
|---|---|---|
| 1 | **Rate Limiter** | Checks client IP against the fixed window (100 req/60 s). Returns `429` if exceeded |
| 2 | **GatewayErrorMiddleware** | Wraps the rest of the pipeline; intercepts `502`/`503`/`504` status codes on the way back |
| 3 | **CorrelationIdMiddleware** | Reads or generates `X-Correlation-ID`, stamps request and response headers |
| 4 | **RequestLoggingMiddleware** | Starts a stopwatch; logs method, path, status, duration after response |
| 5 | **Authentication** | Validates the `Authorization: Bearer <token>` header cryptographically |
| 6 | **Authorization** | Enforces the route's policy (`anonymous` or `authenticated`) |
| 7 | **YARP** | Matches path to a cluster, forwards request, pipes response back to client |

---

## Route Configuration

All routing is managed in [`appsettings.json`](ApiGateway/appsettings.json). No C# changes are required to add a new service.

```json
"ReverseProxy": {
  "Routes": {
    "users-route": {
      "ClusterId": "users-cluster",
      "Match": { "Path": "/api/users/{**catch-all}" },
      "AuthorizationPolicy": "authenticated"
    }
  },
  "Clusters": {
    "users-cluster": {
      "LoadBalancingPolicy": "RoundRobin",
      "Destinations": {
        "node1": { "Address": "http://users-service:8080" }
      }
    }
  }
}
```

**Authorization policies:**
- `"anonymous"` — public routes (e.g., `/api/auth/*`), no token required
- `"authenticated"` — requires a valid, non-expired JWT Bearer token

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Run locally (two terminals)

**Terminal 1 — MockService** (fake downstream, dev only)
```bash
dotnet run --project MockService/MockService.csproj --launch-profile http
# Runs on http://localhost:5100
```

**Terminal 2 — API Gateway**
```bash
dotnet run --project ApiGateway/ApiGateway.csproj --launch-profile http
# Runs on http://localhost:5000
```

### Quick smoke tests

```bash
# 1. Health check
curl http://localhost:5000/health

# 2. Get a real signed JWT (dev helper — MockService only)
curl http://localhost:5100/dev/token?username=abi&role=Admin

# 3. Public route — no token needed
curl -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"demo","password":"password"}'

# 4. Protected route — attach the token from step 2
curl http://localhost:5000/api/users \
  -H "Authorization: Bearer <token>"

# 5. Protected route — no token (expect 401 JSON)
curl http://localhost:5000/api/users

# 6. See Correlation ID flowing through
curl -v http://localhost:5000/api/auth/login \
  -X POST -H "Content-Type: application/json" \
  -d '{"username":"demo","password":"password"}' 2>&1 | grep X-Correlation-ID
```

---

## Standardized Error Responses

All errors from the gateway return a consistent JSON shape — no HTML, no stack traces:

```json
{ "status": 401, "error": "Unauthorized",       "message": "A valid Bearer token is required." }
{ "status": 429, "error": "Too Many Requests",   "message": "You have exceeded the request limit." }
{ "status": 502, "error": "Bad Gateway",         "message": "A downstream service returned an invalid response." }
{ "status": 504, "error": "Gateway Timeout",     "message": "A downstream service did not respond in time." }
```

---

## Log Output

Requests are logged to console and `ApiGateway/logs/gateway-YYYYMMDD.log`:

```
[17:49:54 INF] [2bebaec6-...] GET  /health          → 200 in  82ms
[17:49:55 INF] [254a3727-...] POST /api/auth/login   → 200 in 108ms
[17:49:55 INF] [8d7e2133-...] GET  /api/users        → 200 in 116ms
[17:49:55 WRN] [5defa522-...] GET  /api/users        → 401 in   9ms
```

Log levels are assigned automatically: `5xx → Error`, `4xx → Warning`, `2xx → Information`.
