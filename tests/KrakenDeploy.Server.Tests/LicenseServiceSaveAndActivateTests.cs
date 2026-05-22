using FluentAssertions;
using KrakenDeploy.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Unit tests for <see cref="LicenseService.SaveAndActivateAsync"/>. We can't
/// produce a JWT that the embedded public key will accept (the private key is
/// only with the issuer), so the happy-path "writes file on valid key" branch
/// can't be exercised here. What we CAN pin:
///
///   - The config-override guard fires BEFORE validation (so a malformed
///     paste hits the guard, not a confusing "invalid signature" toast).
///   - Invalid keys do NOT overwrite the existing license file.
///   - Empty / whitespace input throws the right argument exception.
///
/// The end-to-end activate path is integration-tested via the Razor page,
/// not here.
/// </summary>
public sealed class LicenseServiceSaveAndActivateTests : IDisposable
{
    private readonly string _scratchDir;

    public LicenseServiceSaveAndActivateTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"kraken-license-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratchDir, recursive: true); }
        catch { /* test cleanup, swallow */ }
    }

    [Fact]
    public async Task Refuses_when_config_override_present()
    {
        // When License:Key is set in config, persisting the file is a no-op
        // at runtime (LoadAndValidate reads config first). Refusing is the
        // honest move — silently writing a file that won't be honoured
        // would lead the operator to think they activated something.
        var svc = NewService(extras: new Dictionary<string, string?>
        {
            ["License:Key"] = "stub-config-key",
        });

        var act = async () => await svc.SaveAndActivateAsync("any-paste");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*License:Key*");
    }

    [Fact]
    public async Task Refuses_when_env_var_override_present()
    {
        var svc = NewService(extras: new Dictionary<string, string?>
        {
            ["KRAKEN_LICENSE_KEY"] = "stub-env-key",
        });

        var act = async () => await svc.SaveAndActivateAsync("any-paste");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*KRAKEN_LICENSE_KEY*");
    }

    [Fact]
    public async Task Empty_input_throws_argument_exception()
    {
        var svc = NewService();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await svc.SaveAndActivateAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await svc.SaveAndActivateAsync("   "));
    }

    [Fact]
    public async Task Invalid_key_does_not_overwrite_existing_file()
    {
        // Seed a pretend-existing license file. The paste below is malformed
        // (not a JWT) so validation will fail. The file MUST remain intact —
        // otherwise a bad paste silently wipes a working license.
        var licensePath = Path.Combine(_scratchDir, "license.key");
        const string OriginalContent = "previous-license-blob";
        await File.WriteAllTextAsync(licensePath, OriginalContent);

        var svc = NewService();

        var result = await svc.SaveAndActivateAsync("not-a-valid-jwt");

        result.IsValid.Should().BeFalse(
            "the gibberish paste must fail validation");

        File.Exists(licensePath).Should().BeTrue(
            "the failed activation must NOT delete the existing file");

        var contentAfter = await File.ReadAllTextAsync(licensePath);
        contentAfter.Should().Be(OriginalContent,
            "the failed activation must NOT overwrite the existing file");
    }

    [Fact]
    public async Task Invalid_key_returns_result_with_error_message()
    {
        var svc = NewService();

        var result = await svc.SaveAndActivateAsync("clearly-not-a-jwt");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace(
            "the page surfaces this string verbatim to the operator");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private LicenseService NewService(IDictionary<string, string?>? extras = null)
    {
        // Always point Server:DataPath at the scratch dir so the test never
        // writes outside its own sandbox.
        var settings = new Dictionary<string, string?>
        {
            ["Server:DataPath"] = _scratchDir,
        };
        if (extras is not null)
        {
            foreach (var kv in extras) { settings[kv.Key] = kv.Value; }
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        return new LicenseService(config, NullLogger<LicenseService>.Instance);
    }
}
