using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Data.Spaces;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Pure in-memory tests for <see cref="DefaultSpaceContext"/> — no Postgres
/// container required, so these run even when Docker isn't available locally.
/// </summary>
public sealed class DefaultSpaceContextTests
{
    [Fact]
    public void Returns_DefaultSpaceId_with_no_override_in_effect()
    {
        var sut = new DefaultSpaceContext();

        sut.CurrentSpaceId.Should().Be(WellKnown.DefaultSpaceId);
    }

    [Fact]
    public void WithSpace_overrides_returned_id_until_disposed()
    {
        var sut = new DefaultSpaceContext();
        var customSpace = Guid.NewGuid();

        using (sut.WithSpace(customSpace))
        {
            sut.CurrentSpaceId.Should().Be(customSpace);
        }

        sut.CurrentSpaceId.Should().Be(WellKnown.DefaultSpaceId,
            "override is reverted once the scope is disposed");
    }

    [Fact]
    public void WithSpace_supports_nested_overrides_with_LIFO_order()
    {
        var sut = new DefaultSpaceContext();
        var outer = Guid.NewGuid();
        var inner = Guid.NewGuid();

        using (sut.WithSpace(outer))
        {
            sut.CurrentSpaceId.Should().Be(outer);

            using (sut.WithSpace(inner))
            {
                sut.CurrentSpaceId.Should().Be(inner);
            }

            sut.CurrentSpaceId.Should().Be(outer, "inner scope reverts to outer");
        }

        sut.CurrentSpaceId.Should().Be(WellKnown.DefaultSpaceId);
    }

    [Fact]
    public void WithSpace_double_dispose_is_safe()
    {
        var sut = new DefaultSpaceContext();
        var scope = sut.WithSpace(Guid.NewGuid());

        scope.Dispose();
        var act = () => scope.Dispose();
        act.Should().NotThrow();

        sut.CurrentSpaceId.Should().Be(WellKnown.DefaultSpaceId);
    }
}
