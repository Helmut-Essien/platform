using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Platform.Client.Services;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Dtos.Common;
using Platform.Shared.Dtos.Customers;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;
using Xunit;

namespace Client.Tests;

public class PlatformApiClientTests
{
    [Fact]
    public async Task ValidateLicenseAsync_SendsIntegrationKeyHeaderAndPayload()
    {
        var handler = new CapturingHttpMessageHandler(_ =>
            JsonResponse(new ValidateLicenseResponse
            {
                IsValid = true,
                PlanName = "Growth"
            }));
        var client = CreateClient(handler);

        var result = await client.ValidateLicenseAsync("pk_hostel_secret", new ValidateLicenseRequest
        {
            LicenseKey = "HOSTEL-ABCD-1234",
            ServiceCode = "HOSTEL"
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("/api/licenses/validate", handler.LastRequest!.RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Integration-Key", out var values));
        Assert.Equal("pk_hostel_secret", Assert.Single(values));

        var body = JsonSerializer.Deserialize<ValidateLicenseRequest>(
            handler.LastRequestBody!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("HOSTEL-ABCD-1234", body!.LicenseKey);
        Assert.Equal("HOSTEL", body.ServiceCode);
    }

    [Fact]
    public async Task GetLicensesPagedAsync_EscapesCustomerIdInQueryString()
    {
        var handler = new CapturingHttpMessageHandler(_ =>
            JsonResponse(new PagedResult<LicenseDto>
            {
                Items = [],
                TotalCount = 0,
                Page = 2,
                PageSize = 10
            }));
        var client = CreateClient(handler);

        await client.GetLicensesPagedAsync("customer/id with spaces", page: 2, pageSize: 10);

        Assert.Equal(
            "/api/licenses?page=2&pageSize=10&customerId=customer%2Fid%20with%20spaces",
            handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task CreateInvoiceAsync_PostsInvoiceRequestAndReadsResponse()
    {
        var handler = new CapturingHttpMessageHandler(_ =>
            JsonResponse(new InvoiceDto
            {
                Id = "invoice-1",
                CustomerId = "customer-1",
                InvoiceNumber = "INV-2026-00001",
                Status = InvoiceStatus.Sent,
                IssueDate = DateTime.UtcNow,
                Currency = "USD",
                TotalAmount = 125m,
                AmountDue = 125m,
                Receipts = []
            }));
        var client = CreateClient(handler);

        var result = await client.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = "customer-1",
            Status = InvoiceStatus.Sent,
            Currency = "usd",
            Subtotal = 100m,
            TaxAmount = 25m
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("INV-2026-00001", result.Data!.InvoiceNumber);
        Assert.Equal("/api/invoices", handler.LastRequest!.RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);

        var body = await handler.LastRequest.Content!.ReadFromJsonAsync<CreateInvoiceRequest>();
        Assert.Equal("customer-1", body!.CustomerId);
        Assert.Equal(100m, body.Subtotal);
        Assert.Equal(25m, body.TaxAmount);
    }

    [Fact]
    public async Task CreateCustomerAsync_ParsesValidationErrors()
    {
        var handler = new CapturingHttpMessageHandler(_ =>
            JsonResponse(
                new
                {
                    message = "Validation failed.",
                    errors = new Dictionary<string, string[]>
                    {
                        ["ContactEmail"] = ["Enter a valid email address"]
                    }
                },
                HttpStatusCode.BadRequest));
        var client = CreateClient(handler);

        var result = await client.CreateCustomerAsync(new CreateCustomerRequest
        {
            Name = "Acme",
            ContactEmail = "invalid"
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("Validation failed.", result.ErrorMessage);
        Assert.Equal("Enter a valid email address", result.FieldErrors!["ContactEmail"][0]);
    }

    [Fact]
    public async Task GetCustomersPagedAsync_ReturnsEmptyPageWhenJsonBodyIsNull()
    {
        var handler = new CapturingHttpMessageHandler(_ =>
            JsonResponse<PagedResult<CustomerDto>?>(null));
        var client = CreateClient(handler);

        var result = await client.GetCustomersPagedAsync(page: 3, pageSize: 15);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(3, result.Page);
        Assert.Equal(15, result.PageSize);
    }

    private static PlatformApiClient CreateClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://platform.test/")
        };

        return new PlatformApiClient(http);
    }

    private static HttpResponseMessage JsonResponse<T>(T value, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = JsonContent.Create(value, options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
    }

    private sealed class CapturingHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return responder(request);
        }
    }
}
