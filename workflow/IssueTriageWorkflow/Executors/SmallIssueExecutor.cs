using Microsoft.Agents.AI.Workflows;

namespace IssueTriageWorkflow.Executors;

/// <summary>
/// Clear issues of size XS or S. Placeholder for the follow-up steps of small issues; for now it
/// only reports the result.
/// </summary>
internal sealed class SmallIssueExecutor() : Executor<TriageResult>("small-issue", declareCrossRunShareable: true)
{
    public override ValueTask HandleAsync(
        TriageResult result, IWorkflowContext context, CancellationToken cancellationToken = default) =>
        context.YieldOutputAsync(result.Summary(), cancellationToken);

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder).YieldsOutput<string>();
}
