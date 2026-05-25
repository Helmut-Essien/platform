using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Extensions;
using Platform.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddPlatformAuthentication(builder.Configuration);
builder.Services.AddPlatformEmail(builder.Configuration);
builder.Services.AddPlatformRedis(builder.Configuration);

builder.Services.AddCors(options =>
{
    options.AddPolicy("Client", policy =>
    {
        policy.WithOrigins(
                "http://localhost:5154",
                "https://localhost:7296",
                "http://localhost:5173",
                "https://localhost:7173",
                "http://localhost:5174")
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

builder.Services.AddControllers();
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

app.UseHttpsRedirection();

app.UseCors("Client");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
