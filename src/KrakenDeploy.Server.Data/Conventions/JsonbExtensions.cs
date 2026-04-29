using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace KrakenDeploy.Server.Data.Conventions;

/// <summary>
/// Helpers for mapping a property to a PostgreSQL <c>jsonb</c> column with
/// System.Text.Json serialization. Suitable for variable values, scope filters,
/// step inputs, audit payloads, and other aggregate documents that travel together.
/// </summary>
public static class JsonbExtensions
{
    public static PropertyBuilder<T> HasJsonbColumn<T>(this PropertyBuilder<T> builder)
        where T : class
    {
        // Use a named ValueConverter subclass (not a lambda) so the EF Core migration
        // snapshot captures the converter by its stable CLR type name — avoiding the
        // false-positive HasPendingModelChanges() result that occurs when a lambda
        // expression cannot be perfectly round-tripped through the compiled snapshot.
        return builder
            .HasColumnType("jsonb")
            .HasConversion(new JsonbValueConverter<T>());
    }
}

/// <summary>
/// Typed JSON value converter: <typeparamref name="T"/> ↔ <c>string</c> (jsonb column).
/// Declared as a named class so the EF Core migration snapshot can capture and restore
/// the converter by type name rather than serializing a lambda expression.
/// </summary>
public sealed class JsonbValueConverter<T> : ValueConverter<T, string>
    where T : class
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public JsonbValueConverter()
        : base(
            v => JsonSerializer.Serialize(v, Options),
            s => JsonSerializer.Deserialize<T>(s, Options)!)
    {
    }
}
