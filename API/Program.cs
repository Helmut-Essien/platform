using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<IAuditLogService, AuditLogService>();
builder.Services.AddScoped<IBillingService, BillingService>();
builder.Services.AddScoped<ILicenseService, LicenseService>();

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    await db.Database.MigrateAsync();
    await SeedData.SeedAsync(db, logger, app.Environment.IsDevelopment());
    await SeedData.SeedBillingAsync(db);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
