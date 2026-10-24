using GitHubTools;
using Microsoft.Agents.AI.Workflows;

namespace IssueTriageWorkflow.Executors;

/// <summary>
/// Writes the triage result to the issue: the comment, then the triage/ready and size/* labels.
/// A failed label update is reported in the result but does not undo the comment.
/// </summary>
internal sealed class PublishTriageExecutor(GitHubIssueService gitHub)
    : Executor<TriagedIssue>("publish-triage", declareCrossRunShareable: true)
{
    public override async ValueTask HandleAsync(
        TriagedIssue message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var (issue, assessment) = message;

        string commentUrl;
        try
        {
            commentUrl = await gitHub.UpsertTriageCommentAsync(
                issue, assessment.IsClear, assessment.Size, assessment.Rationale, assessment.MissingInformation);
        }
        catch (GitHubIssueException ex)
        {
            await context.YieldOutputAsync($"{issue}: {ex.Message}", cancellationToken);
            return;
        }

        string labels;
        try
        {
            labels = await gitHub.SyncTriageLabelsAsync(issue, assessment.IsClear, assessment.Size);
        }
        catch (GitHubIssueException ex)
        {
            labels = $"not updated: {ex.Message}";
        }

        await context.SendMessageAsync(new TriageResult(issue, assessment, commentUrl, labels), cancellationToken: cancellationToken);
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder).SendsMessage<TriageResult>().YieldsOutput<string>();
}
