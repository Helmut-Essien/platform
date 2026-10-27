namespace Platform.Shared.Dtos.Billing;

/// <summary>Server-side invoice aggregates, grouped per currency (amounts in different currencies are never summed together).</summary>
public class InvoiceStatsDto
{
    public List<InvoiceCurrencyStatsDto> Currencies { get; set; } = [];

    /// <summary>Invoices in Sent/PartiallyPaid/Overdue state across all currencies.</summary>
    public int UnpaidCount { get; set; }

    public int OverdueCount { get; set; }
}

public class InvoiceCurrencyStatsDto
{
    public required string Currency { get; set; }

    /// <summary>Net collected (payments minus reversals) across all invoices.</summary>
    public decimal TotalRevenue { get; set; }

    /// <summary>Remaining balance on Sent/PartiallyPaid/Overdue invoices.</summary>
    public decimal Outstanding { get; set; }

    public int OverdueCount { get; set; }

    /// <summary>Net collected since the first day of the current UTC month.</summary>
    public decimal PaidThisMonth { get; set; }

    public int PaidThisMonthCount { get; set; }
}
