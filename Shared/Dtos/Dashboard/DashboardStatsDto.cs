namespace Platform.Shared.Dtos.Dashboard;

public class DashboardStatsDto
{
    public int CustomerCount { get; set; }

    public int ActiveLicenses { get; set; }

    public int ExpiringWithin30Days { get; set; }

    public int UnpaidInvoices { get; set; }
}
