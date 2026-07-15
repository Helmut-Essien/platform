# Platform

Platform is a .NET 9 SaaS administration platform for managing customers, service products, licenses, billing, integration keys, and audit activity. The solution combines a Blazor WebAssembly admin UI with an ASP.NET Core API, PostgreSQL, Redis, and JWT-based authentication.

## What this application does

The platform is designed for internal admin workflows and supports:

- Customer organization management for onboarding and maintaining tenant or client accounts
- Service catalog administration for defining and controlling the software products or services available to customers
- License issuance, renewal, suspension, and revocation to manage entitlement and access over time
- License validation workflows for integrated services so administrators can verify license state without leaving the admin hub
- Invoice and billing-related operations to track revenue activity and payment status
- Integration key management for connecting services to the platform securely
- Audit log review and monitoring to keep a historical record of important administrative actions
- A dark-themed admin experience tailored for operational dashboards and day-to-day platform oversight

## Core modules

### Customers

The customers area is where administrators manage the organizations or accounts that are using the platform. This module provides a central place to view customer details, understand their subscription or service footprint, and keep their account state organized.

### Services

The services module governs the catalog of products or offerings available in the platform. It allows administrators to define service entries, manage their availability, and relate them to customer access and licensing workflows.

### Licenses

Licenses are the core entitlement layer of the application. This module enables admins to create and manage licenses for customers, track their current status, and take actions such as renewal or revocation when business rules require it.

### Invoices

The invoices section supports billing oversight, including review of invoice records and payment-related activity. It helps the admin team keep track of financial operations in a structured and auditable way.

### Integration keys

Integration keys are used to connect services and external integrations securely. This module makes it easier to generate, inspect, and manage the credentials that allow services to interact with the platform.

### Audit log

The audit log provides a historical view of actions taken across the system. It is useful for troubleshooting, compliance, and monitoring who performed what operation and when.

### Validate license

The validation flow gives administrators a quick way to test and confirm whether a license is behaving as expected. This helps reduce friction when debugging or verifying product activation scenarios.

## Technology stack

- Frontend: Blazor WebAssembly + MudBlazor
- Backend: ASP.NET Core Web API
- Data access: Entity Framework Core + PostgreSQL
- Caching: Redis
- Authentication: ASP.NET Core Identity + JWT
- Testing: xUnit
- Infrastructure: Docker Compose

## Solution structure

- API: backend service and business logic
- Client: Blazor web client UI
- Shared: shared DTOs, constants, and models
- API.Tests: backend unit tests
- Client.Tests: client-side tests
- docs: product and UI specifications

## Prerequisites

Before running the project locally, make sure you have:

- .NET SDK 9
- Docker Desktop or Docker Engine with Compose
- A terminal with access to the repository

## Getting started

### 1. Start supporting services

The repository includes Docker Compose configuration for PostgreSQL and Redis:

```bash
docker compose up -d
```

### 2. Restore dependencies

```bash
dotnet restore Platform.slnx
```

### 3. Run the API

```bash
dotnet run --project API/API.csproj --launch-profile http
```

The API runs by default on http://localhost:5176.

### 4. Run the client

In a second terminal:

```bash
dotnet run --project Client/Client.csproj --launch-profile http
```

The client runs by default on http://localhost:5154.

### 5. Sign in

In development mode, the application seeds an admin user:

- Email: admin@platform.local
- Password: Admin123!

## Database and migrations

The API applies Entity Framework Core migrations automatically on startup. If you need to add a migration manually, use:

```bash
dotnet ef migrations add <MigrationName> --project API/API.csproj
```

## Running tests

Run the test suite with:

```bash
dotnet test Platform.slnx
```

## Configuration notes

The default development configuration is defined in the API configuration files. The app uses:

- PostgreSQL at localhost:5432
- Redis at localhost:6379
- JWT settings from the API configuration

If you plan to run this outside local development, update the configuration values for production security and connectivity.

## License

This project is intended for internal use and is distributed as-is.
