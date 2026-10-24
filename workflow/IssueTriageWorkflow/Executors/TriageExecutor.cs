using System.ComponentModel;
using GitHubTools;
using IssueTriageAgent;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace IssueTriageWorkflow.Executors;

/// <summary>
/// Downloads the repository's code at the pinned commit once, then runs the issue triage agent
/// (agents/IssueTriageAgent) on the issue with read-only tools over that snapshot. The tools are
/// bound to the snapshot in code, so the model can only choose paths and search texts. The snapshot
/// is deleted when the assessment is done.
/// </summary>
internal sealed class TriageExecutor(AIAgent triageAgent, GitHubCodeService gitHubCode)
    : Executor<IssueToTriage>("triage", declareCrossRunShareable: true)
{
    // Upper bound for tool calls per triage, so a confused model cannot loop through the repository.
    private const int MaxToolCalls = 25;

    public override async ValueTask HandleAsync(
        IssueToTriage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        RepositorySnapshot snapshot;
        try
        {
            snapshot = await gitHubCode.DownloadSnapshotAsync(message.Issue, message.CommitSha, cancellationToken);
        }
        catch (GitHubIssueException ex)
        {
            await context.YieldOutputAsync($"{message.Issue}: {ex.Message}", cancellationToken);
            return;
        }

        TriageAssessment assessment;
        using (snapshot)
        {
            try
            {
                assessment = await triageAgent.AssessIssueAsync(message.IssueJson, CreateCodeTools(snapshot), cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                await context.YieldOutputAsync($"{message.Issue}: {ex.Message}", cancellationToken);
                return;
            }
        }

        await context.SendMessageAsync(new TriagedIssue(message.Issue, assessment), cancellationToken: cancellationToken);
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder).SendsMessage<TriagedIssue>().YieldsOutput<string>();

    // Created per run: the tools close over this run's snapshot and call counter, so the executor
    // itself stays stateless.
    private static List<AITool> CreateCodeTools(RepositorySnapshot snapshot)
    {
        var calls = 0;

        // Failures are returned to the model as text, so it can correct a path or try another tool.
        string Invoke(Func<string> call)
        {
            if (Interlocked.Increment(ref calls) > MaxToolCalls)
            {
                return "Tool call budget exhausted. Finish your assessment with the information you have.";
            }

            try
            {
                return call();
            }
            catch (Exception ex) when (ex is GitHubIssueException or IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }

        return
        [
            AIFunctionFactory.Create(
                ([Description("Directory relative to the repository root, e.g. \"src/api\". Empty for the whole repository.")] string? directory) =>
                    Invoke(() => snapshot.ListFiles(directory)),
                name: "list_files",
                description: "Lists the files (path and size) of the repository, optionally only below a directory."),
            AIFunctionFactory.Create(
                ([Description("Text to search for (case-insensitive, not a regex), e.g. a class name, function name or error message.")] string query,
                 [Description("Optional directory to search in, relative to the repository root. Empty for the whole repository.")] string? directory = null) =>
                    Invoke(() => snapshot.SearchCode(query, directory)),
                name: "search_code",
                description: "Searches the text of all files and returns the matching lines as path:line: text (up to 50 matches)."),
            AIFunctionFactory.Create(
                ([Description("File path relative to the repository root, as returned by list_files or search_code.")] string path,
                 [Description("First line to return (1-based). Use it to continue reading a long file.")] int startLine = 1) =>
                    Invoke(() => snapshot.ReadFile(path, startLine)),
                name: "read_file",
                description: "Reads a text file of the repository with line numbers (up to 400 lines per call)."),
        ];
    }
}
