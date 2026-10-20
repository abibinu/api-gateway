# Custom Lightweight API Gateway 🚀

> **Enterprise Reverse Proxy & Traffic Controller**  
> **Lead Engineer:** Abi Binu  

## Overview
The Custom Lightweight API Gateway is a high-performance, centralized network entry point built on **.NET 10** and **YARP (Yet Another Reverse Proxy)**. Designed to sit between client interfaces (Android/Web) and internal backend microservices, it abstracts network complexity by providing a unified Base URL while offloading critical cross-cutting infrastructure concerns.

This gateway prevents internal microservices from being exposed directly to the public internet and eliminates the need to replicate security and rate-limiting logic across multiple backend codebases.

## ⚙️ Core Capabilities

* **Dynamic Request Routing:** Transparently routes incoming HTTP traffic to isolated downstream microservice clusters based on path matching.
* **Centralized JWT Authentication Offloading:** Intercepts and validates JSON Web Tokens (JWT) at the edge. Rejects invalid requests (`401 Unauthorized`) before they ever reach internal servers.
* **Algorithmic Rate Limiting:** Protects downstream APIs from DDoS and brute-force attacks using a Fixed Window limiter policy (e.g., max 100 requests / 60 seconds per IP address).
* **Distributed Tracing (Observability):** Automatically generates and injects an `X-Correlation-ID` header into the pipeline, allowing seamless tracking of a single request across multiple distributed microservices.
* **Standardized JSON Error Handling:** Catches downstream `502 Bad Gateway` or `504 Gateway Timeout` errors and transforms them into predictable JSON structures to prevent mobile client crashes.
* **Configuration-Driven:** All routes, clusters, and load-balancing policies (Round-Robin/Least Requests) are managed via `appsettings.json`, requiring zero C# recompilation to update infrastructure.

## 🛠️ Technology Stack
* **Framework:** .NET 10 Web API
* **Language:** C# 14
* **Proxy Engine:** YARP (Yarp.ReverseProxy)
* **Security:** ASP.NET Core Authentication (`JwtBearer`)
* **Rate Limiting:** `System.Threading.RateLimiting`

## 🚀 System Architecture

```text
Mobile Client (Android/Retrofit) 
       │
       ▼  (HTTPS)
┌───────────────────────────────────────────┐
│           API GATEWAY (.NET 10)           │
│                                           │
│  1. Rate Limiting (Fixed Window)          │
│  2. JWT Validation                        │
│  3. X-Correlation-ID Injection            │
│  4. YARP Routing & Load Balancing         │
└─────────────────────┬─────────────────────┘
                      │ (Internal HTTP/2)
          ┌───────────┼───────────┐
          ▼           ▼           ▼
   [Auth API]   [Users API]  [Inventory API]
