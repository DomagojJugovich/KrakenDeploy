using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Conventions;

/// <summary>
/// Helpers for mapping a property to a PostgreSQL <c>jsonb</c> column with
/// System.Text.Json serialization. Suitable for variable values, scope filters,
/// step inputs, audit payloads, and other aggregate documents that travel together.
/// </summary>
public static class JsonbExtensions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static PropertyBuilder<T> HasJsonbColumn<T>(this PropertyBuilder<T> builder)
        where T : class
    {
        return builder
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, Options),
                v => JsonSerializer.Deserialize<T>(v, Options)!);
    }
}
