using System.Text.Json;
using KrakenDeploy.Server.Core.Domain.Accounts;

namespace KrakenDeploy.ControlPlane.Secrets;

/// <summary>
/// Development / single-host <see cref="ISecretStore"/> backing secrets in a JSON
/// file (<c>{dataPath}/catalog-secrets.json</c>, a <c>ref → value</c> map).
/// <para>
/// <b>Not hardened for production secrets.</b> The file is plaintext; production
/// deployments should swap this for a DPAPI- or vault-backed implementation. The
/// catalog itself only ever stores the reference, never the raw value.
/// </para>
/// </summary>
public sealed class FileSecretStore : ISecretStore, IDisposable
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSecretStore(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        _filePath = Path.Combine(dataPath, "catalog-secrets.json");
    }

    public void Dispose() => _gate.Dispose();

    public async Task<string> ResolveAsync(string secretRef, CancellationToken ct = default)
    {
        var map = await ReadAsync(ct).ConfigureAwait(false);
        return map.TryGetValue(secretRef, out var value)
            ? value
            : throw new KeyNotFoundException(
                $"Secret reference '{secretRef}' was not found in the catalog secret store.");
    }

    public async Task<string> StoreAsync(string secretRef, string secretValue, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map = await ReadUnguardedAsync(ct).ConfigureAwait(false);
            map[secretRef] = secretValue;
            await WriteAsync(map, ct).ConfigureAwait(false);
            return secretRef;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string secretRef, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map = await ReadUnguardedAsync(ct).ConfigureAwait(false);
            if (map.Remove(secretRef))
            {
                await WriteAsync(map, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadUnguardedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadUnguardedAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        await using var stream = File.OpenRead(_filePath);
        var map = await JsonSerializer
            .DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: ct)
            .ConfigureAwait(false);
        return map ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private async Task WriteAsync(Dictionary<string, string> map, CancellationToken ct)
    {
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, map, WriteOptions, ct).ConfigureAwait(false);
    }
}
