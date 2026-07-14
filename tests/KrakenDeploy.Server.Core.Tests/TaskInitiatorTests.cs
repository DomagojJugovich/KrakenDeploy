using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Deployments;

namespace KrakenDeploy.Server.Core.Tests;

/// <summary>
/// Guard behaviour for the task-provenance value object (schema-hardening fix 6).
/// The whole point of the 1-based <see cref="ServerTaskCause"/> + factory design is
/// that a forgotten cause becomes a hard failure at the creation funnel rather than
/// silently persisting as a real value.
/// </summary>
public class TaskInitiatorTests
{
    [Fact]
    public void Default_initiator_is_unset_and_fails_the_guard()
    {
        var initiator = default(TaskInitiator);

        initiator.Cause.Should().Be(ServerTaskCause.Unspecified);
        var act = initiator.EnsureValid;
        act.Should().Throw<InvalidOperationException>("a default TaskInitiator carries no chosen cause");
    }

    [Fact]
    public void StampOnto_rejects_a_default_initiator()
    {
        var task = new Deployment { ReleaseId = Guid.NewGuid() };
        var act = () => default(TaskInitiator).StampOnto(task);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Manual_factory_carries_user_and_display()
    {
        var userId = Guid.NewGuid();
        var initiator = TaskInitiator.Manual(userId, "Alice");

        initiator.Cause.Should().Be(ServerTaskCause.Manual);
        initiator.Display.Should().Be("Alice");
        initiator.UserId.Should().Be(userId);
        initiator.Invoking(i => i.EnsureValid()).Should().NotThrow();
    }

    [Fact]
    public void Automated_factories_have_no_user_but_a_fallback_display()
    {
        var scheduled = TaskInitiator.Scheduled();
        scheduled.Cause.Should().Be(ServerTaskCause.Scheduled);
        scheduled.UserId.Should().BeNull();
        scheduled.Display.Should().NotBeNullOrWhiteSpace();

        var sub = TaskInitiator.Subscription("subscription:1");
        sub.Cause.Should().Be(ServerTaskCause.Subscription);
        sub.UserId.Should().BeNull();
        sub.Detail.Should().Be("subscription:1");
    }

    [Fact]
    public void Blank_display_falls_back_rather_than_persisting_empty()
    {
        // Callers pass Identity.Name ?? Email which can be null; the factory must
        // still yield a non-empty display so the NOT NULL column is satisfied.
        var initiator = TaskInitiator.Api(null, display: null);
        initiator.Display.Should().NotBeNullOrWhiteSpace();
        initiator.Invoking(i => i.EnsureValid()).Should().NotThrow();
    }

    [Fact]
    public void Overlong_display_and_detail_are_truncated_to_the_column_caps()
    {
        var longText = new string('x', TaskInitiator.MaxDisplayLength + 50);
        var initiator = TaskInitiator.Manual(Guid.NewGuid(), longText, detail: longText);

        initiator.Display.Length.Should().Be(TaskInitiator.MaxDisplayLength);
        initiator.Detail!.Length.Should().Be(TaskInitiator.MaxDetailLength);
    }

    [Fact]
    public void ParentStep_inherits_user_and_records_the_parent_id_in_detail()
    {
        var parentUser = Guid.NewGuid();
        var parentTaskId = Guid.NewGuid();
        var initiator = TaskInitiator.ParentStep(parentUser, "Bob", parentTaskId);

        initiator.Cause.Should().Be(ServerTaskCause.ParentStep);
        initiator.UserId.Should().Be(parentUser, "a child deploy attributes to the human who launched the parent");
        initiator.Display.Should().Be("Bob");
        initiator.Detail.Should().Contain(parentTaskId.ToString());
    }

    [Fact]
    public void StampOnto_writes_all_four_provenance_columns()
    {
        var userId = Guid.NewGuid();
        var task = new Deployment { ReleaseId = Guid.NewGuid() };

        TaskInitiator.Mcp(userId, "mcp-user", "retry_deployment;source:x").StampOnto(task);

        task.Cause.Should().Be(ServerTaskCause.Mcp);
        task.CreatedByDisplay.Should().Be("mcp-user");
        task.CreatedByUserId.Should().Be(userId);
        task.CauseDetail.Should().Be("retry_deployment;source:x");
    }
}
