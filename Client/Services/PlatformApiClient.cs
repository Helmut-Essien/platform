using System.Net.Http.Json;
using Platform.Shared.Dtos.Audit;
using Platform.Shared.Dtos.Auth;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Dtos.Customers;
using Platform.Shared.Dtos.IntegrationKeys;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Dtos.ServiceProducts;
using Platform.Shared.Enums;

namespace Platform.Client.Services;

public class PlatformApiClient(HttpClient http)
{
    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken ct = default) =>
        await http.PostAsJsonAsync("api/auth/login", request, ct) is { IsSuccessStatusCode: true } response
            ? await response.Content.ReadFromJsonAsync<LoginResponse>(ct)
            : null;

    public async Task<object?> GetMeAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<object>("api/auth/me", ct);

    public async Task<List<CustomerDto>> GetCustomersAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<CustomerDto>>("api/customers", ct) ?? [];

    public async Task<CustomerDto?> CreateCustomerAsync(CreateCustomerRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/customers", request, ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<CustomerDto>(ct)
            : null;
    }

    public async Task<CustomerDto?> UpdateCustomerAsync(string id, UpdateCustomerRequest request, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync($"api/customers/{id}", request, ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<CustomerDto>(ct)
            : null;
    }

    public async Task<bool> SuspendCustomerAsync(string id, CancellationToken ct = default) =>
        (await http.PostAsync($"api/customers/{id}/suspend", null, ct)).IsSuccessStatusCode;

    public async Task<bool> ReactivateCustomerAsync(string id, CancellationToken ct = default) =>
        (await http.PostAsync($"api/customers/{id}/reactivate", null, ct)).IsSuccessStatusCode;

    public async Task<List<ServiceProductDto>> GetServiceProductsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ServiceProductDto>>("api/serviceproducts", ct) ?? [];

    public async Task<List<LicenseDto>> GetLicensesAsync(string? customerId = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrEmpty(customerId) ? "api/licenses" : $"api/licenses?customerId={customerId}";
        return await http.GetFromJsonAsync<List<LicenseDto>>(url, ct) ?? [];
    }

    public async Task<LicenseDto?> CreateLicenseAsync(CreateLicenseRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/licenses", request, ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<LicenseDto>(ct)
            : null;
    }

    public async Task<bool> ActivateLicenseAsync(string id, ActivateLicenseRequest request, CancellationToken ct = default) =>
        (await http.PostAsJsonAsync($"api/licenses/{id}/activate", request, ct)).IsSuccessStatusCode;

    public async Task<bool> SuspendLicenseAsync(string id, CancellationToken ct = default) =>
        (await http.PostAsync($"api/licenses/{id}/suspend", null, ct)).IsSuccessStatusCode;

    public async Task<bool> RevokeLicenseAsync(string id, CancellationToken ct = default) =>
        (await http.PostAsync($"api/licenses/{id}/revoke", null, ct)).IsSuccessStatusCode;

    public async Task<List<InvoiceDto>> GetInvoicesAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<InvoiceDto>>("api/invoices", ct) ?? [];

    public async Task<List<AuditLogDto>> GetAuditLogsAsync(int limit = 50, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<AuditLogDto>>($"api/audit-logs?limit={limit}", ct) ?? [];

    public async Task<ValidateLicenseResponse?> ValidateLicenseAsync(
        string integrationKey,
        ValidateLicenseRequest request,
        CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/licenses/validate");
        message.Headers.Add("X-Integration-Key", integrationKey);
        message.Content = JsonContent.Create(request);
        var response = await http.SendAsync(message, ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<ValidateLicenseResponse>(ct)
            : null;
    }

    public async Task<List<IntegrationKeyDto>> GetIntegrationKeysAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<IntegrationKeyDto>>("api/integration-keys", ct) ?? [];

    public async Task<CreateIntegrationKeyResponse?> CreateIntegrationKeyAsync(
        string serviceProductId,
        CancellationToken ct = default)
    {
        var response = await http.PostAsync($"api/integration-keys?serviceProductId={serviceProductId}", null, ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<CreateIntegrationKeyResponse>(ct)
            : null;
    }

    public async Task<string?> GetErrorMessageAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        try
        {
            var err = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(ct);
            return err?.GetValueOrDefault("message") ?? response.ReasonPhrase;
        }
        catch
        {
            return response.ReasonPhrase;
        }
    }
}
