using System.Net.Http.Json;
using Platform.Shared.Dtos.Audit;
using Platform.Shared.Dtos.Auth;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Dtos.Common;
using Platform.Shared.Dtos.Customers;
using Platform.Shared.Dtos.Dashboard;
using Platform.Shared.Dtos.IntegrationKeys;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Dtos.ServiceProducts;
using Platform.Shared.Enums;

namespace Platform.Client.Services;

public class PlatformApiClient(HttpClient http)
{
    public async Task<ApiResult<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/auth/login", request, ct);
        return await ToApiResultAsync<LoginResponse>(response, ct);
    }

    public async Task<object?> GetMeAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<object>("api/auth/me", ct);

    public async Task<DashboardStatsDto?> GetDashboardStatsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<DashboardStatsDto>("api/dashboard/stats", ct);

    public async Task<PagedResult<CustomerDto>> GetCustomersPagedAsync(
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default) =>
        await http.GetFromJsonAsync<PagedResult<CustomerDto>>($"api/customers?page={page}&pageSize={pageSize}", ct)
        ?? new PagedResult<CustomerDto> { Items = [], TotalCount = 0, Page = page, PageSize = pageSize };

    public async Task<ApiResult<CustomerDto>> CreateCustomerAsync(
        CreateCustomerRequest request,
        CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/customers", request, ct);
        return await ToApiResultAsync<CustomerDto>(response, ct);
    }

    public async Task<ApiResult<CustomerDto>> UpdateCustomerAsync(
        string id,
        UpdateCustomerRequest request,
        CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync($"api/customers/{id}", request, ct);
        return await ToApiResultAsync<CustomerDto>(response, ct);
    }

    private async Task<ApiResult<T>> ToApiResultAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var data = await response.Content.ReadFromJsonAsync<T>(ct);
            return data is null
                ? ApiResult<T>.Fail("Empty response from server.")
                : ApiResult<T>.Ok(data);
        }

        var message = await GetErrorMessageAsync(response, ct);
        return ApiResult<T>.Fail(message ?? "Request failed.");
    }

    public async Task<bool> SuspendCustomerAsync(string id, CancellationToken ct = default) =>
        (await http.PostAsync($"api/customers/{id}/suspend", null, ct)).IsSuccessStatusCode;

    public async Task<bool> ReactivateCustomerAsync(string id, CancellationToken ct = default) =>
        (await http.PostAsync($"api/customers/{id}/reactivate", null, ct)).IsSuccessStatusCode;

    public async Task<List<ServiceProductDto>> GetServiceProductsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ServiceProductDto>>("api/serviceproducts", ct) ?? [];

    public async Task<ApiResult<ServiceProductDto>> CreateServiceProductAsync(
        CreateServiceProductRequest request,
        CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/serviceproducts", request, ct);
        return await ToApiResultAsync<ServiceProductDto>(response, ct);
    }

    public async Task<ApiResult<ServiceProductDto>> UpdateServiceProductAsync(
        string id,
        UpdateServiceProductRequest request,
        CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync($"api/serviceproducts/{id}", request, ct);
        return await ToApiResultAsync<ServiceProductDto>(response, ct);
    }

    public async Task<PagedResult<LicenseDto>> GetLicensesPagedAsync(
        string? customerId = null,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var url = $"api/licenses?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(customerId))
            url += $"&customerId={Uri.EscapeDataString(customerId)}";

        return await http.GetFromJsonAsync<PagedResult<LicenseDto>>(url, ct)
            ?? new PagedResult<LicenseDto> { Items = [], TotalCount = 0, Page = page, PageSize = pageSize };
    }

    public async Task<ApiResult<LicenseDto>> CreateLicenseAsync(
        CreateLicenseRequest request,
        CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/licenses", request, ct);
        return await ToApiResultAsync<LicenseDto>(response, ct);
    }

    public async Task<bool> ActivateLicenseAsync(string id, ActivateLicenseRequest request, CancellationToken ct = default) =>
        (await http.PostAsJsonAsync($"api/licenses/{id}/activate", request, ct)).IsSuccessStatusCode;

    public async Task<bool> SuspendLicenseAsync(string id, CancellationToken ct = default) =>
        (await http.PostAsync($"api/licenses/{id}/suspend", null, ct)).IsSuccessStatusCode;

    public async Task<bool> RevokeLicenseAsync(string id, CancellationToken ct = default) =>
        (await http.PostAsync($"api/licenses/{id}/revoke", null, ct)).IsSuccessStatusCode;

    public async Task<PagedResult<InvoiceDto>> GetInvoicesPagedAsync(
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default) =>
        await http.GetFromJsonAsync<PagedResult<InvoiceDto>>($"api/invoices?page={page}&pageSize={pageSize}", ct)
        ?? new PagedResult<InvoiceDto> { Items = [], TotalCount = 0, Page = page, PageSize = pageSize };

    public async Task<List<AuditLogDto>> GetAuditLogsAsync(int limit = 50, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<AuditLogDto>>($"api/audit-logs?limit={limit}", ct) ?? [];

    public async Task<ApiResult<ValidateLicenseResponse>> ValidateLicenseAsync(
        string integrationKey,
        ValidateLicenseRequest request,
        CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/licenses/validate");
        message.Headers.Add("X-Integration-Key", integrationKey);
        message.Content = JsonContent.Create(request);
        var response = await http.SendAsync(message, ct);
        return await ToApiResultAsync<ValidateLicenseResponse>(response, ct);
    }

    public async Task<List<IntegrationKeyDto>> GetIntegrationKeysAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<IntegrationKeyDto>>("api/integration-keys", ct) ?? [];

    public async Task<ApiResult<CreateIntegrationKeyResponse>> CreateIntegrationKeyAsync(
        string serviceProductId,
        CancellationToken ct = default)
    {
        var response = await http.PostAsync($"api/integration-keys?serviceProductId={serviceProductId}", null, ct);
        return await ToApiResultAsync<CreateIntegrationKeyResponse>(response, ct);
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
