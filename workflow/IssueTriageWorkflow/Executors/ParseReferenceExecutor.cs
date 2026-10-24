using GitHubTools;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace IssueTriageWorkflow.Executors;

/// <summary>
/// Start of the workflow: extracts the issue reference from the request in code, so the model can
/// never act on any other issue than the one named in the request.
/// </summary>
internal sealed class ParseReferenceExecutor() : ChatProtocolExecutor("parse-reference", declareCrossRunShareable: true)
{
    protected override async ValueTask TakeTurnAsync(
        List<ChatMessage> messages, IWorkflowContext context, bool? emitEvents, CancellationToken cancellationToken = default)
    {
        var request = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;

        if (IssueReference.TryParse(request, out var issue))
        {
            await context.SendMessageAsync(issue, cancellationToken: cancellationToken);
        }
        else
        {
            await context.YieldOutputAsync(
                "Please name the issue to triage, e.g. \"Triage owner/repo#12\" or the issue URL.", cancellationToken);
        }
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder).SendsMessage<IssueReference>().YieldsOutput<string>();
}
