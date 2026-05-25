using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Shared.Dtos.IntegrationKeys;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public class IntegrationKeyService(AppDbContext db, IAuditLogService auditLog) : IIntegrationKeyService
{
    public async Task<IReadOnlyList<IntegrationKeyDto>> ListAsync(
        string? serviceProductId = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.IntegrationKeys.AsNoTracking().Include(k => k.ServiceProduct).AsQueryable();

        if (serviceProductId is not null)
            query = query.Where(k => k.ServiceProductId == serviceProductId);

        var keys = await query.OrderByDescending(k => k.CreatedAt).ToListAsync(cancellationToken);
        return keys.Select(k => ToDto(k)).ToList();
    }

    public async Task<CreateIntegrationKeyResponse> CreateAsync(
        string serviceProductId,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var product = await db.ServiceProducts.FindAsync([serviceProductId], cancellationToken)
            ?? throw new InvalidOperationException("Service product not found.");

        var activeKeys = await db.IntegrationKeys
            .Where(k => k.ServiceProductId == serviceProductId && k.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var existing in activeKeys)
            existing.IsActive = false;

        var plainKey = GenerateIntegrationKey(product.Code);
        var entity = new IntegrationKey
        {
            ServiceProductId = serviceProductId,
            KeyHash = BCrypt.Net.BCrypt.HashPassword(plainKey),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        db.IntegrationKeys.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.IntegrationKeyCreated, performedBy, null, null, null,
            $$"""{"serviceProductId":"{{serviceProductId}}","integrationKeyId":"{{entity.Id}}"}""",
            ipAddress, cancellationToken);

        return new CreateIntegrationKeyResponse
        {
            Key = new IntegrationKeyDto
            {
                Id = entity.Id,
                ServiceProductId = entity.ServiceProductId,
                ServiceProductCode = product.Code,
                IsActive = entity.IsActive,
                CreatedAt = entity.CreatedAt,
                LastUsedAt = entity.LastUsedAt
            },
            PlainKey = plainKey
        };
    }

    public async Task<IntegrationKeyDto> RevokeAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var key = await db.IntegrationKeys.Include(k => k.ServiceProduct)
            .FirstOrDefaultAsync(k => k.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Integration key not found.");

        if (!key.IsActive)
            throw new InvalidOperationException("Integration key is already revoked.");

        key.IsActive = false;
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.IntegrationKeyRevoked, performedBy, null, null, null,
            $$"""{"integrationKeyId":"{{id}}"}""", ipAddress, cancellationToken);

        return ToDto(key);
    }

    private static string GenerateIntegrationKey(string serviceCode)
    {
        var bytes = new byte[24];
        RandomNumberGenerator.Fill(bytes);
        var token = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"pk_{serviceCode.Trim().ToLowerInvariant()}_{token}";
    }

    private static IntegrationKeyDto ToDto(IntegrationKey key, string? code = null) => new()
    {
        Id = key.Id,
        ServiceProductId = key.ServiceProductId,
        ServiceProductCode = code ?? key.ServiceProduct?.Code,
        IsActive = key.IsActive,
        CreatedAt = key.CreatedAt,
        LastUsedAt = key.LastUsedAt
    };
}
