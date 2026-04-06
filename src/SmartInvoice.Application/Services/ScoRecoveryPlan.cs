using System.Linq;

namespace SmartInvoice.Application.Services;

/// <summary>What the background SCO recovery job should do after a user-facing sync.</summary>
public sealed record ScoRecoveryPlan(
    bool ShouldEnqueue,
    bool ResyncFullDateRange,
    IReadOnlyList<string> ScoDetailExternalIds)
{
    public static ScoRecoveryPlan None { get; } = new(false, false, Array.Empty<string>());
}

/// <summary>Decides whether to enqueue background SCO recovery from <see cref="SyncInvoicesResult"/>.</summary>
public interface IScoSyncRecoveryPlanner
{
    /// <param name="includeDetail">Whether the sync that produced <paramref name="syncResult"/> used include-detail.</param>
    ScoRecoveryPlan Plan(SyncInvoicesResult syncResult, bool includeDetail);
}

public sealed class ScoSyncRecoveryPlanner : IScoSyncRecoveryPlanner
{
    public ScoRecoveryPlan Plan(SyncInvoicesResult syncResult, bool includeDetail)
    {
        if (!syncResult.Success)
            return ScoRecoveryPlan.None;

        var listIncomplete = syncResult.ScoListIncomplete;
        var detailIds = syncResult.ScoDetailFailedExternalIds ?? Array.Empty<string>();
        var hasDetailRetries = includeDetail && detailIds.Count > 0;

        if (!listIncomplete && !hasDetailRetries)
            return ScoRecoveryPlan.None;

        return new ScoRecoveryPlan(
            ShouldEnqueue: true,
            ResyncFullDateRange: listIncomplete,
            ScoDetailExternalIds: hasDetailRetries ? detailIds.Distinct(StringComparer.Ordinal).ToList() : Array.Empty<string>());
    }
}
