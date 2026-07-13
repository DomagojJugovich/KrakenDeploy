using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Security;

namespace KrakenDeploy.Server.Core.Tests;

/// <summary>
/// Pure-logic tests for <see cref="RoleAssignmentScopeMatcher.Matches"/>.
/// Per-dimension semantics are subtle (no scope rows = "all", null scope =
/// optimistic match) so each rule has its own focused test. Scope values now
/// live in <see cref="RoleAssignment.Scopes"/>; <see cref="Assignment"/> builds
/// the child rows the old jsonb-array initializers used to set directly.
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

    private static RoleAssignment Assignment(
        IEnumerable<Guid>? projectGroups = null,
        IEnumerable<Guid>? projects = null,
        IEnumerable<Guid>? environments = null,
        IEnumerable<Guid>? tenants = null)
    {
        var a = new RoleAssignment();
        foreach (var id in projectGroups ?? []) { a.Scopes.Add(new RoleAssignmentScope { ProjectGroupId = id }); }
        foreach (var id in projects ?? []) { a.Scopes.Add(new RoleAssignmentScope { ProjectId = id }); }
        foreach (var id in environments ?? []) { a.Scopes.Add(new RoleAssignmentScope { EnvironmentId = id }); }
        foreach (var id in tenants ?? []) { a.Scopes.Add(new RoleAssignmentScope { TenantId = id }); }
        return a;
    }

    // ── Empty assignment ("unscoped" — applies to whole Space) ────────────────

    [Fact]
    public void Unscoped_assignment_matches_any_scope()
    {
        var assignment = new RoleAssignment(); // no scope rows

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
        var assignment = Assignment(projects: [ProjectA, ProjectB]);

        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(ProjectId: ProjectA)).Should().BeTrue();
        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(ProjectId: ProjectB)).Should().BeTrue();
    }

    [Fact]
    public void Project_restricted_assignment_does_not_match_other_project()
    {
        var assignment = Assignment(projects: [ProjectA]);
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
        var assignment = Assignment(projects: [ProjectA]);

        RoleAssignmentScopeMatcher.Matches(assignment, default).Should().BeTrue();
        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(EnvironmentId: Prod)).Should().BeTrue();
    }

    // ── Multi-dimension AND ──────────────────────────────────────────────────

    [Fact]
    public void All_dimensions_must_match_when_all_are_restricted()
    {
        var assignment = Assignment(
            projects: [ProjectA], environments: [Prod], tenants: [TenantX]);

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
        var assignment = Assignment(projects: [ProjectA], environments: [Prod]);

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

    // ── ProjectGroup dimension ────────────────────────────────────────────────

    [Fact]
    public void ProjectGroup_dimension_is_evaluated_with_same_rules()
    {
        var assignment = Assignment(projectGroups: [GroupAlpha]);

        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(ProjectGroupId: GroupAlpha)).Should().BeTrue();
        RoleAssignmentScopeMatcher.Matches(assignment,
            new PermissionScope(ProjectGroupId: Guid.NewGuid())).Should().BeFalse();
        RoleAssignmentScopeMatcher.Matches(assignment, default).Should().BeTrue();
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
        //   Tenants:     (no rows) = all tenants
        var webApp     = Guid.NewGuid();
        var apiGateway = Guid.NewGuid();
        var prod       = Guid.NewGuid();
        var staging    = Guid.NewGuid();

        var assignment = Assignment(
            projects: [webApp, apiGateway], environments: [prod, staging]);

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
        Assignment(projects: [ProjectA]).IsUnscoped.Should().BeFalse();
        Assignment(environments: [Prod]).IsUnscoped.Should().BeFalse();
        Assignment(tenants: [TenantX]).IsUnscoped.Should().BeFalse();
        Assignment(projectGroups: [GroupAlpha]).IsUnscoped.Should().BeFalse();
    }
}
