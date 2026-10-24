using Microsoft.Agents.AI.Workflows;

namespace IssueTriageWorkflow.Executors;

/// <summary>
/// Clear issues of size L or XL. Placeholder for the follow-up steps of large issues; for now it
/// only reports the result.
/// </summary>
internal sealed class LargeIssueExecutor() : Executor<TriageResult>("large-issue", declareCrossRunShareable: true)
{
    public override ValueTask HandleAsync(
        TriageResult result, IWorkflowContext context, CancellationToken cancellationToken = default) =>
        context.YieldOutputAsync(result.Summary(), cancellationToken);

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder).YieldsOutput<string>();
}
