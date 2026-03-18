# auth-service

[![.NET 9](https://img.shields.io/badge/.NET_9-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)
[![Docker](https://img.shields.io/badge/Docker-2496ED?style=for-the-badge&logo=docker&logoColor=white)](https://www.docker.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-4169E1?style=for-the-badge&logo=postgresql&logoColor=white)](https://www.postgresql.org/)
[![JWT](https://img.shields.io/badge/JWT-000000?style=for-the-badge&logo=json-web-tokens&logoColor=white)](https://jwt.io/)
[![MIT License](https://img.shields.io/badge/License-MIT-success?style=for-the-badge)](https://opensource.org/licenses/MIT)

**Authentication microservice** for the [Food Delivery Microservices](https://github.com/dxrkblxss/food-delivery-microservices) project.

---

## ⚡ Features

* User signup / login with Argon2id password hashing
* JWT access tokens (15 min) + long-lived refresh tokens (7 days)
* Secure logout (revokes refresh token)
* Protected `/me` endpoint with claims
* Correlation ID middleware + custom exception handling
* Automatic DB migrations on startup
* Health check endpoint for Docker Compose
* Swagger UI with Bearer token security scheme
* Forwarded headers support (works perfectly behind YARP)

---

## 🧭 Architecture

```mermaid
graph TD
    Gateway[🚀 YARP API Gateway] --> Auth[🔐 Auth Service]

    subgraph "Auth Service"
        Auth[🔐 Auth API] --> AuthDB[(🐘 Auth DB)]
    end

    Client[📱 Client] --> Gateway

    %% Styles
    style Gateway fill:#512BD4,color:#fff
    style AuthDB fill:#4169E1,color:#fff
```

Minimal API endpoints are defined in `Program.cs` + clean folder structure:

```text
textDTOs/      • Models/      • Services/
Repositories/  • Middleware/  • Exceptions/
Options/       • Filters/     • Migrations/
```
