using System.Text.Json;

namespace Platform.Api.Services.Payments;

/// <summary>The few fields we read from a Paystack webhook. Tolerant of missing, null or oddly typed fields.</summary>
public sealed record PaystackWebhookEvent(string Event, string? Reference, string? Domain, long? AmountMinor, string? Currency)
{
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 32 };

    public static PaystackWebhookEvent? TryParse(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body, ParseOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var name = String(root, "event");
            if (string.IsNullOrWhiteSpace(name))
                return null;

            string? reference = null, domain = null, currency = null;
            long? amount = null;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                // charge.*: data.reference; refund.*: data.transaction_reference; dispute.*: data.transaction.reference
                reference = String(data, "reference") ?? String(data, "transaction_reference");
                if (reference is null && data.TryGetProperty("transaction", out var tx) && tx.ValueKind == JsonValueKind.Object)
                    reference = String(tx, "reference");
                domain = String(data, "domain");
                currency = String(data, "currency");
                if (data.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt64(out var v))
                    amount = v;
            }

            return new PaystackWebhookEvent(name.Trim(), reference?.Trim(), domain?.Trim(), amount, currency?.Trim().ToUpperInvariant());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
