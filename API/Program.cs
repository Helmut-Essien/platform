using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Extensions;
using Platform.Api.Services;
using Platform.Api.Services.Email;

var builder = WebApplication.CreateBuilder(args);

// Forwarded headers are only honoured from explicitly configured proxies/networks.
// IIS in-process hosting (MonsterASP) needs none; behind a reverse proxy set
// ForwardedHeaders__Enabled=true plus ForwardedHeaders__KnownProxies__0 / KnownNetworks__0 (CIDR).
var forwardedHeadersEnabled = ForwardedHeadersSetup.Configure(builder.Services, builder.Configuration);

var rawConnectionString = new[]
    {
        builder.Configuration.GetConnectionString("DefaultConnection"),
        Environment.GetEnvironmentVariable("DB_CONNECTION"),
        Environment.GetEnvironmentVariable("DATABASE_URL")
    }
    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

var connectionString = rawConnectionString?.Trim();

if (builder.Environment.IsDevelopment())
{
    Console.Error.WriteLine(
        $"[Startup] Database connection resolved (len={connectionString?.Length ?? -1}).");
}

if (string.IsNullOrEmpty(connectionString))
    throw new InvalidOperationException(
        "Database connection string is missing. Set ConnectionStrings__DefaultConnection, DB_CONNECTION, or DATABASE_URL.");

if (connectionString.StartsWith("postgres", StringComparison.OrdinalIgnoreCase))
{
    var uri = new Uri(connectionString);
    var userInfo = uri.UserInfo.Split(':');
    var username = Uri.UnescapeDataString(userInfo[0]);
    var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
    var database = uri.AbsolutePath.TrimStart('/');
    var port = uri.Port > 0 ? uri.Port : 5432;
    connectionString = $"Host={uri.Host};Port={port};Database={database};Username={username};Password={password}";
    if (builder.Environment.IsDevelopment())
        Console.Error.WriteLine($"[Startup] Converted postgres URI → Host={uri.Host};Port={port};Database={database};Username={username}");
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql =>
        npgsql.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorCodesToAdd: null)));

// Persist the Data Protection key ring in Postgres. On IIS the default key store is ephemeral
// (no user profile), so restarts invalidated password-reset/confirmation tokens and other protected data.
builder.Services.AddDataProtection()
    .SetApplicationName("Platform.Api")
    .PersistKeysToDbContext<AppDbContext>();

builder.Services.Configure<AuthSettings>(builder.Configuration.GetSection(AuthSettings.SectionName));
builder.Services.AddPlatformAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddPlatformEmail(builder.Configuration, builder.Environment);
builder.Services.AddPlatformRedis(builder.Configuration, builder.Environment);
builder.Services.Configure<LifecycleSettings>(
    builder.Configuration.GetSection(LifecycleSettings.SectionName));
builder.Services.AddHostedService<LicenseExpiryReminderWorker>();
builder.Services.AddHostedService<OverdueInvoiceLifecycleWorker>();
builder.Services.AddPlatformPayments(builder.Configuration);

builder.Services.AddCors(options =>
{
    options.AddPolicy("Client", policy =>
    {
        var configOrigins = CorsOrigins.Resolve(builder.Configuration);
        string[] origins;
        if (builder.Environment.IsDevelopment())
        {
            var devOrigins = new[]
            {
                "http://localhost:5154",
                "https://localhost:7296",
                "http://localhost:5173",
                "https://localhost:7173",
                "http://localhost:5174"
            };
            origins = configOrigins.Concat(devOrigins).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        else
        {
            origins = configOrigins;
        }

        if (origins.Length == 0)
        {
            // Non-dev without explicit origins: allow same-origin hosted WASM only (no cross-origin).
            policy.SetIsOriginAllowed(_ => false);
            return;
        }

        policy.WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddScoped<IAdminAuthService, AdminAuthService>();
builder.Services.AddScoped<IAuditLogService, AuditLogService>();
builder.Services.AddScoped<IAuditLogQueryService>(sp => (AuditLogService)sp.GetRequiredService<IAuditLogService>());
builder.Services.AddScoped<IBillingService, BillingService>();
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddScoped<IServiceProductService, ServiceProductService>();
builder.Services.AddScoped<ILicenseService, LicenseService>();
builder.Services.AddScoped<IIntegrationKeyService, IntegrationKeyService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IInvoiceBrandService, InvoiceBrandService>();

builder.Services.AddExceptionHandler<Platform.Api.Http.PlatformExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddPlatformControllers();
builder.Services.AddPlatformRateLimiting(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddPlatformResponseCompression();
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(180);
    options.IncludeSubDomains = false;
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Platform.Api.Identity.ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var adminSeed = scope.ServiceProvider.GetRequiredService<IOptions<AdminSeedSettings>>();

    var migrateOnStartup = app.Configuration.GetValue<bool?>("Database:MigrateOnStartup")
        ?? app.Environment.IsDevelopment();
    if (migrateOnStartup)
        await DatabaseStartup.MigrateWithRetryAsync(db, logger);
    else
        logger.LogInformation("Database:MigrateOnStartup is off; apply migrations with `dotnet ef database update` or the idempotent SQL script.");

    // Demo catalogue, demo customer/invoice and hard-coded integration keys are Development-only.
    if (app.Environment.IsDevelopment())
    {
        await SeedData.SeedAsync(db, logger, isDevelopment: true);
        await SeedData.SeedBillingAsync(db);
    }

    await IdentitySeedData.SeedAdminAsync(
        userManager,
        roleManager,
        adminSeed,
        app.Environment,
        logger);
}

if (!app.Environment.IsDevelopment())
{
    var clientBaseUrl = app.Configuration["Auth:ClientBaseUrl"];
    if (string.IsNullOrWhiteSpace(clientBaseUrl)
        || clientBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
        || clientBaseUrl.Contains("127.0.0.1", StringComparison.Ordinal))
    {
        app.Logger.LogWarning(
            "Auth:ClientBaseUrl is '{ClientBaseUrl}'. Password-reset links in emails will not work; set Auth__ClientBaseUrl to the public site URL.",
            clientBaseUrl);
    }
}

app.LogPaymentsConfiguration();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
if (forwardedHeadersEnabled)
    app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    if (app.Configuration.GetValue("Https:Redirect", true))
        app.UseHttpsRedirection();
}

// Hosted Blazor WASM client (same origin as the API). Unknown file types are not served.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles(StaticFileCaching.Options());
app.UsePlatformResponseCompression();

app.UseCors("Client");

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();

// Anonymous, cheap liveness probe for keep-alive pings (IIS idle timeout) and load balancers.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous()
    .DisableRateLimiting();

// Readiness probe that also checks the database connection.
app.MapGet("/health/db", async (AppDbContext db, CancellationToken ct) =>
        await db.Database.CanConnectAsync(ct)
            ? Results.Ok(new { status = "ok", database = "ok" })
            : Results.Json(new { status = "degraded", database = "unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous()
    .DisableRateLimiting();

// Unknown /api/* routes must stay 404s instead of returning the SPA shell.
app.MapFallback("/api/{**slug}", () => Results.NotFound());
app.MapFallbackToFile("index.html", StaticFileCaching.Options());

app.Run();
