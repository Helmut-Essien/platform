using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Security;
using Platform.Api.Services;
using Platform.Api.Services.Licensing;

namespace API.Tests;

internal static class LicenseValidationTestFactory
{
    internal sealed class TestEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static readonly Lazy<string> TestKeyPem = new(() => LicenseTokenSigner.GenerateKeyPair().PrivatePem);

    internal static LicensingSettings Settings(Action<LicensingSettings>? configure = null)
    {
        var s = new LicensingSettings { TokenSigningKey = TestKeyPem.Value, TokenSigningKeyId = "test-kid" };
        configure?.Invoke(s);
        return s;
    }

    internal static LicenseTokenSigner Signer(LicensingSettings settings, string environment = "Production") =>
        new(Options.Create(settings), new TestEnv(environment), NullLogger<LicenseTokenSigner>.Instance);

    internal static LicenseValidationService Create(
        AppDbContext db,
        LicensingSettings? settings = null,
        IntegrationKeyCache? cache = null,
        LicenseTokenSigner? signer = null,
        TimeProvider? clock = null,
        LicenseEmailAuditThrottle? emailAuditThrottle = null)
    {
        settings ??= Settings();
        return new LicenseValidationService(
            db,
            cache ?? new IntegrationKeyCache(Options.Create(settings)),
            signer ?? Signer(settings),
            Options.Create(settings),
            Options.Create(new RedisSettings()),
            NullLogger<LicenseValidationService>.Instance,
            clock,
            emailAuditThrottle);
    }
}
