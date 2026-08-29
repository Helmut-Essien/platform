using System.Net.Http.Json;
using Platform.Shared.Dtos.Audit;
using Platform.Shared.Dtos.Auth;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Dtos.Common;
using Platform.Shared.Dtos.Customers;
using Platform.Shared.Dtos.Dashboard;
using Platform.Shared.Dtos.Email;
using Platform.Shared.Dtos.IntegrationKeys;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Dtos.ServiceProducts;
using Platform.Shared.Dtos.Settings;
using Platform.Shared.Enums;

namespace Platform.Client.Services;

public class PlatformApiClient(HttpClient http)
{
    public async Task<ApiResult<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/auth/login", request, ct);
        return await ToApiResultAsync<LoginResponse>(response, ct);
    }

    public async Task<ApiResult<object>> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/auth/forgot-password", request, ct);
        return await ToApiResultAsync<object>(response, ct);
    }

    public async Task<ApiResult<object>> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/auth/reset-password", request, ct);
        return await ToApiResultAsync<object>(response, ct);
    }

    public async Task<LoginResponse?> GetMeAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<LoginResponse>("api/auth/me", ct);

    public async Task<DashboardStatsDto?> GetDashboardStatsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<DashboardStatsDto>("api/dashboard/stats", ct);

    public async Task<PagedResult<CustomerDto>> GetCustomersPagedAsync(
        int page = 1,
        int pageSize = 25,
        string? search = null,
        string? status = null,
        string? created = null,
        CancellationToken ct = default)
    {
        var url = $"api/customers?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(search))
            url += $"&search={Uri.EscapeDataString(search.Trim())}";
        if (!string.IsNullOrWhiteSpace(status) && status != "all")
            url += $"&status={Uri.EscapeDataString(status)}";
        if (!string.IsNullOrWhiteSpace(created) && created != "all")
            url += $"&created={Uri.EscapeDataString(created)}";

        return await http.GetFromJsonAsync<PagedResult<CustomerDto>>(url, ct)
            ?? new PagedResult<CustomerDto> { Items = [], TotalCount = 0, Page = page, PageSize = pageSize };
    }

    public async Task<CustomerDto?> GetCustomerAsync(string id, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<CustomerDto>($"api/customers/{id}", ct);

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

    public async Task<ApiResult<CustomerDto>> SuspendCustomerAsync(string id, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"api/customers/{id}/suspend", null, ct);
        return await ToApiResultAsync<CustomerDto>(response, ct);
    }

    public async Task<ApiResult<CustomerDto>> ReactivateCustomerAsync(string id, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"api/customers/{id}/reactivate", null, ct);
        return await ToApiResultAsync<CustomerDto>(response, ct);
    }

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

    public async Task<(bool Success, string? ErrorMessage)> DeleteServiceProductAsync(string id, CancellationToken ct = default)
    {
        var response = await http.DeleteAsync($"api/serviceproducts/{id}", ct);
        if (response.IsSuccessStatusCode)
            return (true, null);

        var errorMessage = await GetErrorMessageAsync(response, ct);
        return (false, errorMessage ?? "Failed to delete service product.");
    }

    public async Task<PagedResult<LicenseDto>> GetLicensesPagedAsync(
        string? customerId = null,
        int page = 1,
        int pageSize = 25,
        int? expiringWithinDays = null,
        CancellationToken ct = default)
    {
        var url = $"api/licenses?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(customerId))
            url += $"&customerId={Uri.EscapeDataString(customerId)}";
        if (expiringWithinDays is > 0)
            url += $"&expiringWithinDays={expiringWithinDays.Value}";

        return await http.GetFromJsonAsync<PagedResult<LicenseDto>>(url, ct)
            ?? new PagedResult<LicenseDto> { Items = [], TotalCount = 0, Page = page, PageSize = pageSize };
    }

    public async Task<LicenseDto?> GetLicenseAsync(string id, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<LicenseDto>($"api/licenses/{id}", ct);

    public async Task<ApiResult<LicenseDto>> CreateLicenseAsync(
        CreateLicenseRequest request,
        CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/licenses", request, ct);
        return await ToApiResultAsync<LicenseDto>(response, ct);
    }

    public async Task<ApiResult<LicenseDto>> UpdateLicenseAsync(
        string id,
        UpdateLicenseRequest request,
        CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync($"api/licenses/{id}", request, ct);
        return await ToApiResultAsync<LicenseDto>(response, ct);
    }

    public async Task<ApiResult<LicenseDto>> ActivateLicenseAsync(
        string id,
        ActivateLicenseRequest request,
        CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync($"api/licenses/{id}/activate", request, ct);
        return await ToApiResultAsync<LicenseDto>(response, ct);
    }

    public async Task<ApiResult<LicenseDto>> RenewLicenseAsync(
        string id,
        RenewLicenseRequest request,
        CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync($"api/licenses/{id}/renew", request, ct);
        return await ToApiResultAsync<LicenseDto>(response, ct);
    }

    public async Task<ApiResult<LicenseDto>> SuspendLicenseAsync(string id, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"api/licenses/{id}/suspend", null, ct);
        return await ToApiResultAsync<LicenseDto>(response, ct);
    }

    public async Task<ApiResult<LicenseDto>> RevokeLicenseAsync(string id, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"api/licenses/{id}/revoke", null, ct);
        return await ToApiResultAsync<LicenseDto>(response, ct);
    }

    public async Task<ApiResult<LicenseDto>> RotateLicenseKeyAsync(string id, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"api/licenses/{id}/rotate-key", null, ct);
        return await ToApiResultAsync<LicenseDto>(response, ct);
    }

    public Task<ApiResult<LicenseDto>> ResendLicenseKeyAsync(string id, CancellationToken ct = default) =>
        RotateLicenseKeyAsync(id, ct);

    public async Task<PagedResult<InvoiceDto>> GetInvoicesPagedAsync(
        string? customerId = null,
        int page = 1,
        int pageSize = 25,
        bool unpaidOnly = false,
        CancellationToken ct = default)
    {
        var url = $"api/invoices?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(customerId))
            url += $"&customerId={Uri.EscapeDataString(customerId)}";
        if (unpaidOnly)
            url += "&unpaidOnly=true";

        return await http.GetFromJsonAsync<PagedResult<InvoiceDto>>(url, ct)
            ?? new PagedResult<InvoiceDto> { Items = [], TotalCount = 0, Page = page, PageSize = pageSize };
    }

    public async Task<InvoiceDto?> GetInvoiceAsync(string id, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<InvoiceDto>($"api/invoices/{id}", ct);

    public async Task<ApiResult<InvoiceDto>> CreateInvoiceAsync(
        CreateInvoiceRequest request,
        CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/invoices", request, ct);
        return await ToApiResultAsync<InvoiceDto>(response, ct);
    }

    public async Task<ApiResult<InvoiceDto>> VoidInvoiceAsync(string id, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"api/invoices/{id}/void", null, ct);
        return await ToApiResultAsync<InvoiceDto>(response, ct);
    }

    public async Task<ApiResult<InvoiceDto>> SendInvoiceAsync(string id, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"api/invoices/{id}/send", null, ct);
        return await ToApiResultAsync<InvoiceDto>(response, ct);
    }

    public async Task<List<EmailDeliveryDto>> GetEmailDeliveriesAsync(
        string? customerId = null,
        string? licenseId = null,
        string? invoiceId = null,
        CancellationToken ct = default)
    {
        var url = "api/email-deliveries?limit=100";
        if (!string.IsNullOrEmpty(customerId))
            url += $"&customerId={Uri.EscapeDataString(customerId)}";
        if (!string.IsNullOrEmpty(licenseId))
            url += $"&licenseId={Uri.EscapeDataString(licenseId)}";
        if (!string.IsNullOrEmpty(invoiceId))
            url += $"&invoiceId={Uri.EscapeDataString(invoiceId)}";
        return await http.GetFromJsonAsync<List<EmailDeliveryDto>>(url, ct) ?? [];
    }

    public async Task<ApiResult<EmailDeliveryDto>> RetryEmailDeliveryAsync(string id, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"api/email-deliveries/{id}/retry", null, ct);
        return await ToApiResultAsync<EmailDeliveryDto>(response, ct);
    }

    public async Task<ApiResult<ReceiptDto>> RecordReceiptAsync(
        string invoiceId,
        RecordReceiptRequest request,
        CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync($"api/invoices/{invoiceId}/receipts", request, ct);
        return await ToApiResultAsync<ReceiptDto>(response, ct);
    }

    public async Task<ApiResult<ReceiptDto>> ReverseReceiptAsync(
        string invoiceId,
        string receiptId,
        ReverseReceiptRequest request,
        CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync(
            $"api/invoices/{invoiceId}/receipts/{receiptId}/reverse",
            request,
            ct);
        return await ToApiResultAsync<ReceiptDto>(response, ct);
    }

    public async Task<List<AuditLogDto>> GetAuditLogsAsync(
        string? customerId = null,
        string? licenseId = null,
        AuditAction? action = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        var url = $"api/audit-logs?limit={limit}";
        if (!string.IsNullOrEmpty(customerId))
            url += $"&customerId={Uri.EscapeDataString(customerId)}";
        if (!string.IsNullOrEmpty(licenseId))
            url += $"&licenseId={Uri.EscapeDataString(licenseId)}";
        if (action is not null)
            url += $"&action={action}";

        return await http.GetFromJsonAsync<List<AuditLogDto>>(url, ct) ?? [];
    }

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

    public async Task<List<IntegrationKeyDto>> GetIntegrationKeysAsync(
        string? serviceProductId = null,
        CancellationToken ct = default)
    {
        var url = "api/integration-keys";
        if (!string.IsNullOrEmpty(serviceProductId))
            url += $"?serviceProductId={Uri.EscapeDataString(serviceProductId)}";

        return await http.GetFromJsonAsync<List<IntegrationKeyDto>>(url, ct) ?? [];
    }

    public async Task<ApiResult<CreateIntegrationKeyResponse>> CreateIntegrationKeyAsync(
        string serviceProductId,
        CancellationToken ct = default)
    {
        var response = await http.PostAsync($"api/integration-keys?serviceProductId={serviceProductId}", null, ct);
        return await ToApiResultAsync<CreateIntegrationKeyResponse>(response, ct);
    }

    public async Task<ApiResult<IntegrationKeyDto>> RevokeIntegrationKeyAsync(string id, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"api/integration-keys/{id}/revoke", null, ct);
        return await ToApiResultAsync<IntegrationKeyDto>(response, ct);
    }

    public async Task<InvoiceBrandDto?> GetInvoiceBrandAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<InvoiceBrandDto>("api/invoice-brand", ct);

    public async Task<ApiResult<InvoiceBrandDto>> UpdateInvoiceBrandAsync(
        UpdateInvoiceBrandRequest request,
        CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync("api/invoice-brand", request, ct);
        return await ToApiResultAsync<InvoiceBrandDto>(response, ct);
    }

    public async Task<string?> GetInvoiceBrandLogoDataUrlAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/invoice-brand/logo", ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0)
            return null;

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
        return $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
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

        var (message, fieldErrors) = await ParseErrorAsync(response, ct);
        return ApiResult<T>.Fail(message ?? "Request failed.", fieldErrors);
    }

    public async Task<(string? Message, IReadOnlyDictionary<string, string[]>? FieldErrors)> ParseErrorAsync(
        HttpResponseMessage response,
        CancellationToken ct = default)
    {
        try
        {
            var err = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(ct);
            if (err is null)
                return (response.ReasonPhrase, null);

            IReadOnlyDictionary<string, string[]>? fieldErrors = err.Errors is { Count: > 0 }
                ? err.Errors
                : null;

            return (err.Message ?? response.ReasonPhrase, fieldErrors);
        }
        catch
        {
            return (response.ReasonPhrase, null);
        }
    }

    public async Task<string?> GetErrorMessageAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        var (message, _) = await ParseErrorAsync(response, ct);
        return message;
    }
}
