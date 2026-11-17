using Microsoft.Extensions.Configuration;
using Platform.Api.Configuration;
using Xunit;

namespace API.Tests;

/// <summary>The committed Billing section of API/appsettings.json, and the startup check of bad values.</summary>
public class BillingSettingsTests
{
    private static IConfigurationSection CommittedBillingSection()
    {
        var repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(PublicSiteTests.ClientWwwRoot()))!; // <root>/Client/wwwroot
        var appsettings = Path.Combine(repoRoot, "API", "appsettings.json");
        Assert.True(File.Exists(appsettings), appsettings);
        return new ConfigurationBuilder().AddJsonFile(appsettings, optional: false).Build().GetSection(BillingSettings.SectionName);
    }

    [Fact]
    public void CommittedAppSettings_BindToBillingSettings_WithNoUnknownKeys_AndPassTheStartupCheck()
    {
        var section = CommittedBillingSection();
        Assert.True(section.Exists());
        // A misspelt key (it would silently fall back to the code default) or a non-number fails here, before deploy.
        var settings = section.Get<BillingSettings>(o => o.ErrorOnUnknownConfiguration = true)!;
        Assert.Equal("GHS", settings.DefaultCurrency);
        Assert.Equal(30, settings.PriceChangeNoticeDays);
        Assert.Empty(BillingSettingsCheck.Problems(settings));
    }

    [Fact]
    public void AnUnreadableValue_FailsToBind()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:PriceChangeNoticeDays"] = "thirty"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => config.GetSection(BillingSettings.SectionName).Get<BillingSettings>());
    }

    [Theory]
    [InlineData("US", 30, "DefaultCurrency")]
    [InlineData("GH5", 30, "DefaultCurrency")]
    [InlineData("", 30, "DefaultCurrency")]
    [InlineData("GHS", -5, "outside 0–365; 0 is used")]
    [InlineData("GHS", 400, "outside 0–365; 365 is used")]
    [InlineData("GHS", 7, "shorter than Billing:RenewalInvoiceDaysBefore")]
    public void StartupCheck_ReportsValuesThatAreCorrectedOrSurprising(string currency, int noticeDays, string expected)
    {
        var problems = BillingSettingsCheck.Problems(new BillingSettings { DefaultCurrency = currency, PriceChangeNoticeDays = noticeDays });
        Assert.Contains(problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ghs", 30)]
    [InlineData(" USD ", 0)]
    [InlineData("GHS", 14)]
    [InlineData("GHS", 365)]
    public void StartupCheck_AcceptsUsableValues(string currency, int noticeDays) =>
        Assert.Empty(BillingSettingsCheck.Problems(new BillingSettings { DefaultCurrency = currency, PriceChangeNoticeDays = noticeDays }));
}
