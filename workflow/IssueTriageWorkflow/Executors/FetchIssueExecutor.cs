using GitHubTools;
using Microsoft.Agents.AI.Workflows;

namespace IssueTriageWorkflow.Executors;

/// <summary>
/// Reads the issue from GitHub and pins the head commit of the default branch, so every source
/// code read during the triage sees the same snapshot.
/// </summary>
internal sealed class FetchIssueExecutor(GitHubIssueService gitHub, GitHubCodeService gitHubCode)
    : Executor<IssueReference>("fetch-issue", declareCrossRunShareable: true)
{
    public override async ValueTask HandleAsync(
        IssueReference issue, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        string issueJson;
        string commitSha;
        try
        {
            issueJson = await gitHub.GetIssueAsync(issue);
            (_, commitSha) = await gitHubCode.GetDefaultBranchHeadAsync(issue);
        }
        catch (GitHubIssueException ex)
        {
            await context.YieldOutputAsync($"{issue}: {ex.Message}", cancellationToken);
            return;
        }

        await context.SendMessageAsync(new IssueToTriage(issue, issueJson, commitSha), cancellationToken: cancellationToken);
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder).SendsMessage<IssueToTriage>().YieldsOutput<string>();
}
