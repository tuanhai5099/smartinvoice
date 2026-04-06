using SmartInvoice.Application.Services;
using Xunit;

namespace SmartInvoice.Application.Tests;

public class ScoSyncRecoveryPlannerTests
{
    private readonly ScoSyncRecoveryPlanner _planner = new();

    [Fact]
    public void Plan_WhenSyncFailed_ReturnsNone()
    {
        var r = new SyncInvoicesResult(false, "err", 0);
        var p = _planner.Plan(r, includeDetail: true);
        Assert.False(p.ShouldEnqueue);
        Assert.False(p.ResyncFullDateRange);
        Assert.Empty(p.ScoDetailExternalIds);
    }

    [Fact]
    public void Plan_WhenSuccessAndNoScoIssues_ReturnsNone()
    {
        var r = new SyncInvoicesResult(true, null, 10, null, false, null);
        var p = _planner.Plan(r, includeDetail: true);
        Assert.False(p.ShouldEnqueue);
    }

    [Fact]
    public void Plan_WhenScoListIncomplete_EnqueuesResync()
    {
        var r = new SyncInvoicesResult(true, "warn", 5, null, ScoListIncomplete: true, null);
        var p = _planner.Plan(r, includeDetail: false);
        Assert.True(p.ShouldEnqueue);
        Assert.True(p.ResyncFullDateRange);
        Assert.Empty(p.ScoDetailExternalIds);
    }

    [Fact]
    public void Plan_WhenScoDetailFailedAndIncludeDetail_EnqueuesDetailIds()
    {
        var ids = new[] { "a", "b" };
        var r = new SyncInvoicesResult(true, null, 3, ids, false, ids);
        var p = _planner.Plan(r, includeDetail: true);
        Assert.True(p.ShouldEnqueue);
        Assert.False(p.ResyncFullDateRange);
        Assert.Equal(2, p.ScoDetailExternalIds.Count);
        Assert.Contains("a", p.ScoDetailExternalIds);
    }

    [Fact]
    public void Plan_WhenScoDetailFailedButIncludeDetailFalse_DoesNotEnqueueDetailOnly()
    {
        var ids = new[] { "x" };
        var r = new SyncInvoicesResult(true, null, 1, ids, false, ids);
        var p = _planner.Plan(r, includeDetail: false);
        Assert.False(p.ShouldEnqueue);
    }

    [Fact]
    public void Plan_WhenBothListIncompleteAndDetail_EnqueuesBothFlags()
    {
        var ids = new[] { "id1" };
        var r = new SyncInvoicesResult(true, "w", 2, ids, ScoListIncomplete: true, ids);
        var p = _planner.Plan(r, includeDetail: true);
        Assert.True(p.ShouldEnqueue);
        Assert.True(p.ResyncFullDateRange);
        Assert.Single(p.ScoDetailExternalIds);
    }

    [Fact]
    public void Plan_DeduplicatesDetailIds()
    {
        var ids = new[] { "a", "a", "b" };
        var r = new SyncInvoicesResult(true, null, 1, ids, false, ids);
        var p = _planner.Plan(r, includeDetail: true);
        Assert.Equal(2, p.ScoDetailExternalIds.Count);
    }
}
