using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Radzen;
using Radzen.Blazor;

// Lives in the parent Components namespace (not .Shared) because CA1716
// forbids hand-written namespaces ending in the reserved word 'Shared'.
namespace KrakenDeploy.Server.Components;

/// <summary>
/// <see cref="RadzenDataGrid{TItem}"/> that persists the user's grid settings
/// (column widths/order/visibility, sort, filters, page size) to browser
/// localStorage under <c>kraken-grid:{SettingsKey}</c>.
///
/// Persistence is opt-in per grid via <see cref="SettingsKey"/>; without a key
/// the component behaves exactly like a plain RadzenDataGrid. Consumer-supplied
/// <c>Settings</c>/<c>SettingsChanged</c>/<c>LoadSettings</c> bindings win —
/// the auto-persistence only attaches when those are unbound.
///
/// Mechanics (verified against Radzen 10.3.2 source): any user change incl.
/// column resize calls <c>SaveSettings()</c>, which fires
/// <see cref="RadzenDataGrid{TItem}.SettingsChanged"/> when it has a delegate;
/// <see cref="RadzenDataGrid{TItem}.LoadSettings"/> is re-invoked on every
/// <c>OnAfterRenderAsync</c> and applies <c>args.Settings</c> when the
/// reference differs from the grid's current settings — hence
/// <see cref="_persisted"/> must track the latest value or old settings would
/// revert user changes on the next render.
///
/// Note: only columns with a <c>Property</c> or a <c>UniqueID</c> are captured
/// — give template-only columns a <c>UniqueID</c> if their width should stick.
/// </summary>
public class KrakenGrid<TItem> : RadzenDataGrid<TItem>
    where TItem : notnull
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>Stable storage key for this grid (e.g. "targets"). Shared
    /// across all rows/instances of the page; omit to disable persistence.</summary>
    [Parameter] public string? SettingsKey { get; set; }

    private DataGridSettings? _persisted;

    private string StorageKey => $"kraken-grid:{SettingsKey}";

    /// <summary>
    /// Bump when the persisted format or its handling changes. Stored entries
    /// with a different (or missing) version are discarded on load, so
    /// browsers holding payloads from older builds self-heal instead of
    /// re-applying state the current code can't handle (a stale pre-versioning
    /// entry could wedge a grid by reverting every user change on render).
    /// </summary>
    private const int SettingsVersion = 1;

    private sealed record SettingsEnvelope(int V, DataGridSettings? S);

    protected override void OnInitialized()
    {
        // App-wide default: text filters match case-insensitively. Guarded so
        // a consumer that explicitly binds the parameter still wins (the
        // explicit-Default case is indistinguishable from unset — acceptable,
        // nothing in the app wants case-sensitive filtering).
        if (FilterCaseSensitivity == FilterCaseSensitivity.Default)
        {
            FilterCaseSensitivity = FilterCaseSensitivity.CaseInsensitive;
        }

        if (!string.IsNullOrEmpty(SettingsKey))
        {
            // Unsupplied parameters are never touched by SetParametersAsync,
            // so these self-assignments survive re-renders; when the consumer
            // binds their own, parameter binding overwrites ours — theirs win.
            LoadSettings ??= args =>
            {
                if (_persisted is not null)
                {
                    args.Settings = _persisted;
                }
            };

            if (!SettingsChanged.HasDelegate)
            {
                SettingsChanged = EventCallback.Factory.Create<DataGridSettings>(this, PersistAsync);
            }
        }

        base.OnInitialized();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !string.IsNullOrEmpty(SettingsKey))
        {
            try
            {
                var json = await JS.InvokeAsync<string?>("localStorage.getItem", StorageKey);
                if (!string.IsNullOrEmpty(json))
                {
                    var envelope = JsonSerializer.Deserialize<SettingsEnvelope>(json);
                    if (envelope is { V: SettingsVersion, S: not null })
                    {
                        _persisted = envelope.S;
                        NormalizeFilterValues(_persisted);
                    }
                    else
                    {
                        // Unknown/older format — drop it so it can't wedge the grid.
                        await JS.InvokeVoidAsync("localStorage.removeItem", StorageKey);
                    }
                }
            }
            catch
            {
                // Prerender / storage unavailable — grid keeps declared layout.
            }
        }

        // Base invokes LoadSettings and applies _persisted when it changed.
        await base.OnAfterRenderAsync(firstRender);
    }

    /// <summary>
    /// System.Text.Json round-trips <c>object</c>-typed filter values as
    /// <see cref="JsonElement"/>, which Radzen can't apply back to typed
    /// columns — convert them to native CLR types so restoring saved filters
    /// works instead of silently failing (or throwing) on grids with
    /// filtering enabled.
    /// </summary>
    private static void NormalizeFilterValues(DataGridSettings? settings)
    {
        if (settings?.Columns is null)
        {
            return;
        }

        foreach (var column in settings.Columns)
        {
            column.FilterValue = Normalize(column.FilterValue);
            column.SecondFilterValue = Normalize(column.SecondFilterValue);
        }
    }

    private static object? Normalize(object? value)
    {
        if (value is not JsonElement element)
        {
            return value;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.TryGetDateTimeOffset(out var dto) ? dto : element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True   => true,
            JsonValueKind.False  => false,
            JsonValueKind.Array  => element.EnumerateArray().Select(e => Normalize(e)).ToList(),
            _                    => null,
        };
    }

    private async Task PersistAsync(DataGridSettings settings)
    {
        // Keep the LoadSettings source in sync FIRST, so re-renders never
        // re-apply stale settings over what the user just changed.
        _persisted = settings;

        try
        {
            await JS.InvokeVoidAsync(
                "localStorage.setItem", StorageKey,
                JsonSerializer.Serialize(new SettingsEnvelope(SettingsVersion, settings)));
        }
        catch
        {
            // Persistence failure shouldn't break the grid.
        }
    }
}
