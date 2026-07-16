using FluentAssertions;
using KrakenDeploy.Server.Data;
using Npgsql;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// C3/T1-19 — pure unit tests for the connection-string pool cap. No Postgres
/// required (deliberately outside the Docker collection).
/// </summary>
public sealed class NpgsqlConnectionStringsTests
{
    private const string Base = "Host=localhost;Database=kraken;Username=u;Password=p";

    [Fact]
    public void WithMaxPoolSize_applies_the_cap_when_none_is_set()
    {
        var result = NpgsqlConnectionStrings.WithMaxPoolSize(Base, 50);

        new NpgsqlConnectionStringBuilder(result).MaxPoolSize.Should().Be(50);
    }

    [Fact]
    public void WithMaxPoolSize_respects_an_operator_supplied_value()
    {
        var withPool = Base + ";Maximum Pool Size=80";

        var result = NpgsqlConnectionStrings.WithMaxPoolSize(withPool, 50);

        new NpgsqlConnectionStringBuilder(result).MaxPoolSize.Should().Be(80);
    }

    [Fact]
    public void WithMaxPoolSize_respects_the_MaxPoolSize_alias_spelling()
    {
        // Npgsql canonicalizes the "MaxPoolSize" alias to "Maximum Pool Size";
        // the helper's single ContainsKey check must cover both.
        var withAlias = Base + ";MaxPoolSize=80";

        var result = NpgsqlConnectionStrings.WithMaxPoolSize(withAlias, 50);

        new NpgsqlConnectionStringBuilder(result).MaxPoolSize.Should().Be(80);
    }

    [Fact]
    public void WithMaxPoolSize_is_idempotent()
    {
        var once = NpgsqlConnectionStrings.WithMaxPoolSize(Base, 50);
        var twice = NpgsqlConnectionStrings.WithMaxPoolSize(once, 50);

        new NpgsqlConnectionStringBuilder(twice).MaxPoolSize.Should().Be(50);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WithMaxPoolSize_rejects_a_non_positive_cap(int cap)
    {
        var act = () => NpgsqlConnectionStrings.WithMaxPoolSize(Base, cap);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
