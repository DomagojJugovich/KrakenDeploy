using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KrakenDeploy.Cli;

// ── Lightweight DTOs ──────────────────────────────────────────────────────────

public sealed record ProjectDto(Guid Id, string Name, string Slug, string? Description);

public sealed record EnvironmentDto(Guid Id, string Name, string Slug);

public sealed record TargetDto(
    Guid Id,
    string Name,
    string Status,
    string? MachineName,
    string? OperatingSystem,
    string? AgentVersion,
    List<string> Roles,
    DateTimeOffset? LastSeenUtc);

public sealed record ReleaseDto(
    Guid Id,
    Guid ProjectId,
    string Version,
    string? ReleaseNotes,
    DateTimeOffset CreatedUtc);

public sealed record DeploymentDto(
    Guid Id,
    string Status,
    Guid ReleaseId,
    Guid EnvironmentId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc);

public sealed record LogEntryDto(int Sequence, DateTimeOffset Timestamp, string Level, string Message);

// ── Client ────────────────────────────────────────────────────────────────────

/// <summary>
/// Thin wrapper around <see cref="HttpClient"/> that adds the <c>X-Api-Key</c>
/// header and provides typed helpers for every CLI operation.
/// </summary>
public sealed class KrakenApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public KrakenApiClient(string serverUrl, string apiKey)
    {
        _http = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        // Identify the client kind so the server attributes deployments/runs to
        // cause=Cli rather than the generic cause=Api (the API-key principal alone
        // is indistinguishable from a raw REST caller).
        _http.DefaultRequestHeaders.Add(
            Contracts.KrakenHttpHeaders.ClientKind, Contracts.KrakenHttpHeaders.ClientKindCli);
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ── Projects ──────────────────────────────────────────────────────────────

    public async Task<List<ProjectDto>> GetProjectsAsync(CancellationToken ct = default)
        => await GetRequiredAsync<List<ProjectDto>>("api/projects", ct).ConfigureAwait(false);

    public async Task<ProjectDto?> GetProjectBySlugAsync(string slug, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/projects/by-slug/{Uri.EscapeDataString(slug)}", ct)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProjectDto>(JsonOpts, ct).ConfigureAwait(false);
    }

    // ── Environments ──────────────────────────────────────────────────────────

    public async Task<List<EnvironmentDto>> GetEnvironmentsAsync(CancellationToken ct = default)
        => await GetRequiredAsync<List<EnvironmentDto>>("api/environments", ct).ConfigureAwait(false);

    // ── Targets ───────────────────────────────────────────────────────────────

    public async Task<List<TargetDto>> GetTargetsAsync(CancellationToken ct = default)
        => await GetRequiredAsync<List<TargetDto>>("api/targets", ct).ConfigureAwait(false);

    // ── Packages ──────────────────────────────────────────────────────────────

    public async Task<JsonObject> UploadPackageAsync(
        string packageId, string version, string filePath, CancellationToken ct = default)
    {
        using var form    = new MultipartFormDataContent();
        using var fileStream = File.OpenRead(filePath);
        using var fileContent = new StreamContent(fileStream);

        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(new StringContent(packageId), "packageId");
        form.Add(new StringContent(version),   "version");
        form.Add(fileContent, "file", Path.GetFileName(filePath));

        var response = await _http.PostAsync("api/packages/upload", form, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(JsonOpts, ct)
            .ConfigureAwait(false))!;
    }

    // ── Releases ──────────────────────────────────────────────────────────────

    public async Task<List<ReleaseDto>> GetReleasesAsync(Guid projectId, CancellationToken ct = default)
        => await GetRequiredAsync<List<ReleaseDto>>(
            $"api/projects/{projectId}/releases", ct).ConfigureAwait(false);

    public async Task<ReleaseDto> CreateReleaseAsync(
        Guid projectId,
        string version,
        string? notes,
        Dictionary<string, string>? packageVersions,
        CancellationToken ct = default)
    {
        var body = new
        {
            Version        = version,
            ReleaseNotes   = notes,
            PackageVersions = packageVersions,
        };
        var response = await _http.PostAsJsonAsync(
            $"api/projects/{projectId}/releases", body, JsonOpts, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReleaseDto>(JsonOpts, ct)
            .ConfigureAwait(false))!;
    }

    // ── Deployments ───────────────────────────────────────────────────────────

    public async Task<DeploymentDto> CreateDeploymentAsync(
        Guid releaseId,
        Guid environmentId,
        Guid targetId,
        IReadOnlyDictionary<string, string>? promptedValues = null,
        CancellationToken ct = default)
    {
        var body = new
        {
            ReleaseId = releaseId,
            EnvironmentId = environmentId,
            TargetId = targetId,
            PromptedValues = promptedValues,
        };
        var response = await _http.PostAsJsonAsync("api/deployments", body, JsonOpts, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeploymentDto>(JsonOpts, ct)
            .ConfigureAwait(false))!;
    }

    public async Task<DeploymentDto?> GetDeploymentAsync(Guid id, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"api/deployments/{id}", ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DeploymentDto>(JsonOpts, ct)
            .ConfigureAwait(false);
    }

    public async Task<List<LogEntryDto>> GetDeploymentLogsAsync(
        Guid id, int from = 0, CancellationToken ct = default)
        => await GetRequiredAsync<List<LogEntryDto>>(
            $"api/deployments/{id}/logs?from={from}", ct).ConfigureAwait(false);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<T> GetRequiredAsync<T>(string path, CancellationToken ct)
    {
        var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(JsonOpts, ct).ConfigureAwait(false))!;
    }

    public void Dispose() => _http.Dispose();
}
