using GitHubTools;
using IssueTriageWorkflow.Executors;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace IssueTriageWorkflow;

/// <summary>
/// parse-reference → fetch-issue → triage (agent) → publish-triage → switch:
///   not clear → not-clear | XS, S → small-issue | L, XL → large-issue
/// </summary>
public static class IssueTriageWorkflowFactory
{
    public static Workflow Build(AIAgent triageAgent, GitHubIssueService gitHub, GitHubCodeService gitHubCode, string name)
    {
        // Every executor is stateless and shareable, so the hosted workflow can serve parallel requests.
        var parseReference = new ParseReferenceExecutor();
        var fetchIssue = new FetchIssueExecutor(gitHub, gitHubCode);
        var triage = new TriageExecutor(triageAgent, gitHubCode);
        var publishTriage = new PublishTriageExecutor(gitHub);
        var notClear = new NotClearExecutor();
        var smallIssue = new SmallIssueExecutor();
        var largeIssue = new LargeIssueExecutor();

        var builder = new WorkflowBuilder(parseReference)
            .WithName(name)
            .AddEdge(parseReference, fetchIssue)
            .AddEdge(fetchIssue, triage)
            .AddEdge(triage, publishTriage);

        builder.AddSwitch(publishTriage, cases => cases
            .AddCase<TriageResult>(r => r is { Assessment.IsClear: false }, [notClear])
            .AddCase<TriageResult>(r => r?.Assessment.Size is "XS" or "S", [smallIssue])
            .WithDefault([largeIssue]));

        // Each executor yields a one-line message when the run ends there (result or error).
        return builder
            .WithOutputFrom(parseReference, fetchIssue, triage, publishTriage, notClear, smallIssue, largeIssue)
            .Build();
    }
}
