using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Extensions;
using Platform.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var rawConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("DB_CONNECTION")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL");

var connectionString = rawConnectionString?.Trim();

Console.Error.WriteLine($"[Startup] Resolved (len={connectionString?.Length ?? -1}) = '{connectionString?[..Math.Min(60, connectionString.Length)]}...'");

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
    Console.Error.WriteLine($"[Startup] Converted URI to key=value: {connectionString}");
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddPlatformAuthentication(builder.Configuration);
builder.Services.AddPlatformEmail(builder.Configuration);
builder.Services.AddPlatformRedis(builder.Configuration);

builder.Services.AddCors(options =>
{
    options.AddPolicy("Client", policy =>
    {
        var configOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
        var devOrigins = new[]
        {
            "http://localhost:5154",
            "https://localhost:7296",
            "http://localhost:5173",
            "https://localhost:7173",
            "http://localhost:5174"
        };
        var allOrigins = configOrigins.Concat(devOrigins).Distinct().ToArray();

        policy.WithOrigins(allOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddScoped<IAuditLogService, AuditLogService>();
builder.Services.AddScoped<IBillingService, BillingService>();
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddScoped<IServiceProductService, ServiceProductService>();
builder.Services.AddScoped<ILicenseService, LicenseService>();
builder.Services.AddScoped<IIntegrationKeyService, IntegrationKeyService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IInvoiceBrandService, InvoiceBrandService>();

builder.Services.AddPlatformControllers();
builder.Services.AddPlatformRateLimiting(builder.Configuration);
builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Platform.Api.Identity.ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var adminSeed = scope.ServiceProvider.GetRequiredService<IOptions<AdminSeedSettings>>();

    await db.Database.MigrateAsync();
    await SeedData.SeedAsync(db, logger, app.Environment.IsDevelopment());
    await SeedData.SeedBillingAsync(db);
    await IdentitySeedData.SeedAdminAsync(
        userManager,
        roleManager,
        adminSeed,
        app.Environment,
        logger);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseForwardedHeaders();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    ServeUnknownFileTypes = true
});

app.UseCors("Client");

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
