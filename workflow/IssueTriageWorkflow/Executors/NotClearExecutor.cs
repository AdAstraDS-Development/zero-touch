using Microsoft.Agents.AI.Workflows;

namespace IssueTriageWorkflow.Executors;

/// <summary>The issue needs more information: the comment asks the author, nothing else to do.</summary>
internal sealed class NotClearExecutor() : Executor<TriageResult>("not-clear", declareCrossRunShareable: true)
{
    public override ValueTask HandleAsync(
        TriageResult result, IWorkflowContext context, CancellationToken cancellationToken = default) =>
        context.YieldOutputAsync(result.Summary(), cancellationToken);

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder).YieldsOutput<string>();
}
