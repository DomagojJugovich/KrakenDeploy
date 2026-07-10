using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Security;

namespace KrakenDeploy.Server.Core.Tests;

/// <summary>
/// Pure-logic tests for <see cref="RoleAssignmentScopeMatcher.Matches"/>.
/// Per-dimension semantics are subtle (empty list = "all", null scope =
/// optimistic match) so each rule has its own focused test.
/// </summary>
public sealed class RoleAssignmentScopeMatcherTests
{
    private static readonly Guid ProjectA   = Guid.NewGuid();
    private static readonly Guid ProjectB   = Guid.NewGuid();
    private static readonly Guid Prod       = Guid.NewGuid();
    private static readonly Guid Staging    = Guid.NewGuid();
    private static readonly Guid TenantX    = Guid.NewGuid();
    private static readonly Guid TenantY    = Guid.NewGuid();
    private static readonly Guid GroupAlpha = Guid.NewGuid();

    // ── Empty assignment ("unscoped" — applies to whole Space) ────────────────

    [Fact]
    public void Unscoped_assignment_matches_any_scope()
    {
        var assignment = new RoleAssignment(); // all dimension lists empty

        // Empty scope, fully-pinned scope, partially-pinned scope all match.
        RoleAssignmentScopeMatcher.Matches(assignment, default).Should().BeTrue();
        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(ProjectId: ProjectA)).Should().BeTrue();
        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(
                ProjectId: ProjectA, EnvironmentId: Prod, TenantId: TenantX))
            .Should().BeTrue();
    }

    // ── Single-dimension restriction ──────────────────────────────────────────

    [Fact]
    public void Project_restricted_assignment_matches_when_scope_is_in_list()
    {
        var assignment = new RoleAssignment { ProjectIds = [ProjectA, ProjectB] };

        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(ProjectId: ProjectA)).Should().BeTrue();
        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(ProjectId: ProjectB)).Should().BeTrue();
    }

    [Fact]
    public void Project_restricted_assignment_does_not_match_other_project()
    {
        var assignment = new RoleAssignment { ProjectIds = [ProjectA] };
        var unrelated  = Guid.NewGuid();

        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(ProjectId: unrelated)).Should().BeFalse();
    }

    [Fact]
    public void Project_restricted_assignment_matches_when_scope_does_not_pin_a_project()
    {
        // Optimistic: caller hasn't asked about a specific project, so an
        // assignment that grants on SOME project should still contribute its
        // permissions (UI uses this for "do I see this menu?" decisions).
        var assignment = new RoleAssignment { ProjectIds = [ProjectA] };

        RoleAssignmentScopeMatcher.Matches(assignment, default).Should().BeTrue();
        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(EnvironmentId: Prod)).Should().BeTrue();
    }

    // ── Multi-dimension AND ──────────────────────────────────────────────────

    [Fact]
    public void All_dimensions_must_match_when_all_are_restricted()
    {
        var assignment = new RoleAssignment
        {
            ProjectIds     = [ProjectA],
            EnvironmentIds = [Prod],
            TenantIds      = [TenantX],
        };

        // All three pinned and matching → match.
        RoleAssignmentScopeMatcher.Matches(assignment, new PermissionScope(
            ProjectId: ProjectA, EnvironmentId: Prod, TenantId: TenantX))
            .Should().BeTrue();

        // One dimension mismatched → no match.
        RoleAssignmentScopeMatcher.Matches(assignment, new PermissionScope(
            ProjectId: ProjectA, EnvironmentId: Staging, TenantId: TenantX))
            .Should().BeFalse();

        RoleAssignmentScopeMatcher.Matches(assignment, new PermissionScope(
            ProjectId: ProjectB, EnvironmentId: Prod, TenantId: TenantX))
            .Should().BeFalse();

        RoleAssignmentScopeMatcher.Matches(assignment, new PermissionScope(
            ProjectId: ProjectA, EnvironmentId: Prod, TenantId: TenantY))
            .Should().BeFalse();
    }

    [Fact]
    public void Restricted_dimension_with_unrestricted_query_passes_optimistically()
    {
        // "Web Deployers can deploy WebApp+ApiGateway in Prod+Staging for any
        // tenant." Caller asks: "Can they deploy ANYTHING?" → yes (because
        // there exists a project/env where they could).
        var assignment = new RoleAssignment
        {
            ProjectIds     = [ProjectA],
            EnvironmentIds = [Prod],
            // TenantIds: empty → all
        };

        // Caller pins nothing: optimistic match across both restricted dims.
        RoleAssignmentScopeMatcher.Matches(assignment, default).Should().BeTrue();

        // Caller pins one matching dim: still matches (other dim's
        // restriction is "passed" because caller didn't ask about it).
        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(ProjectId: ProjectA)).Should().BeTrue();

        // Caller pins one mismatched dim: no match.
        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(ProjectId: ProjectB)).Should().BeFalse();
    }

    // ── ProjectGroup + TenantTag dimensions ──────────────────────────────────

    [Fact]
    public void ProjectGroup_dimension_is_evaluated_with_same_rules()
    {
        var assignment = new RoleAssignment { ProjectGroupIds = [GroupAlpha] };

        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(ProjectGroupId: GroupAlpha)).Should().BeTrue();
        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(ProjectGroupId: Guid.NewGuid())).Should().BeFalse();
        RoleAssignmentScopeMatcher.Matches(assignment, default).Should().BeTrue();
    }

    [Fact]
    public void Tag_dimension_is_evaluated_with_same_rules()
    {
        var tagId = Guid.NewGuid();
        var assignment = new RoleAssignment { TagIds = [tagId] };

        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(TagId: tagId)).Should().BeTrue();
        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(TagId: Guid.NewGuid())).Should().BeFalse();
    }

    // ── Argument validation ──────────────────────────────────────────────────

    [Fact]
    public void Matches_throws_on_null_assignment()
    {
        var act = () => RoleAssignmentScopeMatcher.Matches(null!, default);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Octopus-doc-style example ────────────────────────────────────────────

    [Fact]
    public void Octopus_style_example_from_RoleAssignment_xml_doc()
    {
        // Mirrors the example in RoleAssignment.cs's XML doc:
        //   Team:        Web Deployers
        //   Role:        Project Deployer
        //   Projects:    [WebApp, ApiGateway]
        //   Environments:[Prod, Staging]
        //   Tenants:     [] = all tenants
        var webApp     = Guid.NewGuid();
        var apiGateway = Guid.NewGuid();
        var prod       = Guid.NewGuid();
        var staging    = Guid.NewGuid();

        var assignment = new RoleAssignment
        {
            ProjectIds     = [webApp, apiGateway],
            EnvironmentIds = [prod, staging],
            // TenantIds: empty → all
        };

        // "Deploy WebApp to Prod for any tenant" → match.
        RoleAssignmentScopeMatcher.Matches(assignment, new PermissionScope(
            ProjectId: webApp, EnvironmentId: prod, TenantId: Guid.NewGuid()))
            .Should().BeTrue();

        // "Deploy ApiGateway to Staging for tenant X" → match.
        RoleAssignmentScopeMatcher.Matches(assignment, new PermissionScope(
            ProjectId: apiGateway, EnvironmentId: staging, TenantId: Guid.NewGuid()))
            .Should().BeTrue();

        // "Deploy WebApp to Dev (not in env list)" → no match.
        var dev = Guid.NewGuid();
        RoleAssignmentScopeMatcher.Matches(assignment, new PermissionScope(
            ProjectId: webApp, EnvironmentId: dev))
            .Should().BeFalse();

        // "Deploy SomeOtherProject to Prod" → no match.
        RoleAssignmentScopeMatcher.Matches(assignment, new PermissionScope(
            ProjectId: Guid.NewGuid(), EnvironmentId: prod))
            .Should().BeFalse();
    }

    // ── IsUnscoped sanity ────────────────────────────────────────────────────

    [Fact]
    public void Empty_assignment_reports_IsUnscoped()
    {
        new RoleAssignment().IsUnscoped.Should().BeTrue();
    }

    [Fact]
    public void Assignment_with_any_dimension_set_is_not_IsUnscoped()
    {
        new RoleAssignment { ProjectIds = [ProjectA] }.IsUnscoped.Should().BeFalse();
        new RoleAssignment { EnvironmentIds = [Prod] }.IsUnscoped.Should().BeFalse();
        new RoleAssignment { TenantIds = [TenantX] }.IsUnscoped.Should().BeFalse();
        new RoleAssignment { TagIds = [Guid.NewGuid()] }.IsUnscoped.Should().BeFalse();
        new RoleAssignment { ProjectGroupIds = [GroupAlpha] }.IsUnscoped.Should().BeFalse();
    }
}
