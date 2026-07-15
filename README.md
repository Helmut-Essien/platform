# Platform

Platform is a .NET 9 SaaS administration platform for managing customers, service products, licenses, billing, integration keys, and audit activity. The solution combines a Blazor WebAssembly admin UI with an ASP.NET Core API, PostgreSQL, Redis, and JWT-based authentication.

## What this application does

The platform is designed for internal admin workflows and supports:

- Customer organization management
- Service catalog administration
- License issuance, renewal, suspension, and revocation
- License validation workflows for integrated services
- Invoice and billing-related operations
- Integration key management
- Audit log review and monitoring
- A dark-themed admin experience for day-to-day operations

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
