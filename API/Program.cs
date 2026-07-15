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

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

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
app.UseHttpsRedirection();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors("Client");

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
