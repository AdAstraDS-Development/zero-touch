using GitHubTools;
using IssueTriageAgent;

namespace IssueTriageWorkflow;

// Messages passed between the executors of the issue triage workflow.

/// <summary>
/// The issue to assess, as JSON read from GitHub, and the commit of the repository's default
/// branch whose source code the agent may read.
/// </summary>
public sealed record IssueToTriage(IssueReference Issue, string IssueJson, string CommitSha);

/// <summary>The agent's assessment of an issue, not yet published.</summary>
public sealed record TriagedIssue(IssueReference Issue, TriageAssessment Assessment);

/// <summary>The published triage result; the size-specific branches start from here.</summary>
public sealed record TriageResult(IssueReference Issue, TriageAssessment Assessment, string CommentUrl, string Labels)
{
    public string Summary() => Assessment.IsClear
        ? $"{Issue}: clear, size {Assessment.Size}. Comment: {CommentUrl} (labels: {Labels})"
        : $"{Issue}: needs more information ({Assessment.MissingInformation?.Length ?? 0} questions). Comment: {CommentUrl} (labels: {Labels})";
}
