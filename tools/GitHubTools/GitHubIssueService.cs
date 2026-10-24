using System.Text;
using System.Text.Json;
using Octokit;

namespace GitHubTools;

/// <summary>
/// Reads GitHub issues and writes the triage result (comment and labels). Used by workflow
/// executors, not by the model: the comment and labels are produced here in code, so their
/// format is always the same.
/// </summary>
public sealed class GitHubIssueService(GitHubAppClientFactory clientFactory, string[] allowedRepositories)
{
    // Hidden marker used to find (and update) our own comment instead of posting duplicates
    // when an issue is triaged again, e.g. after the author added the missing information.
    private const string CommentMarker = "<!-- zero-touch-issue-triage -->";

    // Labels managed by the triage: ready + size when the issue is clear, needs-info while it is not.
    // The GitHub trigger stops re-triaging an issue once it has the ready label.
    public const string ReadyLabel = "triage/ready";
    public const string NeedsInfoLabel = "triage/needs-info";
    public const string SizeLabelPrefix = "size/";

    private const int MaxBodyLength = 20_000;
    private const int MaxCommentLength = 4_000;
    private const int MaxComments = 30;

    /// <summary>
    /// Reads the issue title, body, state, labels, author and discussion comments as JSON.
    /// </summary>
    public async Task<string> GetIssueAsync(IssueReference issue)
    {
        EnsureAllowed(issue);

        try
        {
            var gitHub = await clientFactory.GetClientForRepositoryAsync(issue.Owner, issue.Repo);

            var details = await gitHub.Issue.Get(issue.Owner, issue.Repo, issue.Number);
            if (details.PullRequest is not null)
            {
                throw new GitHubIssueException($"{issue} is a pull request, not an issue.");
            }

            var comments = await gitHub.Issue.Comment.GetAllForIssue(issue.Owner, issue.Repo, issue.Number);

            return JsonSerializer.Serialize(new
            {
                reference = issue.ToString(),
                title = details.Title,
                state = details.State.StringValue,
                author = details.User?.Login,
                createdAt = details.CreatedAt,
                labels = details.Labels.Select(l => l.Name),
                body = Truncate(details.Body ?? "(empty)", MaxBodyLength),
                comments = comments
                    .Where(c => !IsTriageComment(c))
                    .TakeLast(MaxComments)
                    .Select(c => new
                    {
                        author = c.User?.Login,
                        createdAt = c.CreatedAt,
                        body = Truncate(c.Body ?? "", MaxCommentLength),
                    }),
            });
        }
        catch (ApiException ex)
        {
            throw new GitHubIssueException($"GitHub returned {(int)ex.StatusCode} for {issue}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Posts the triage result as a comment, or updates the previous triage comment if one exists.
    /// Returns the comment URL.
    /// </summary>
    public async Task<string> UpsertTriageCommentAsync(
        IssueReference issue, bool isClear, string? size, string rationale, IReadOnlyList<string>? missingInformation)
    {
        EnsureAllowed(issue);

        try
        {
            var body = RenderComment(isClear, size, rationale, missingInformation);
            var gitHub = await clientFactory.GetClientForRepositoryAsync(issue.Owner, issue.Repo);

            var existing = (await gitHub.Issue.Comment.GetAllForIssue(issue.Owner, issue.Repo, issue.Number))
                .LastOrDefault(IsTriageComment);

            var comment = existing is null
                ? await gitHub.Issue.Comment.Create(issue.Owner, issue.Repo, issue.Number, body)
                : await gitHub.Issue.Comment.Update(issue.Owner, issue.Repo, existing.Id, body);

            return comment.HtmlUrl;
        }
        catch (ApiException ex)
        {
            throw new GitHubIssueException($"GitHub returned {(int)ex.StatusCode} while writing the comment on {issue}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Sets the triage labels: <see cref="ReadyLabel"/> and <c>size/&lt;size&gt;</c> when the issue is
    /// clear, <see cref="NeedsInfoLabel"/> otherwise, and removes the other managed labels (including
    /// any outdated size label). Labels that do not exist in the repository yet are created by GitHub.
    /// Returns a summary such as "+triage/ready, +size/S, -triage/needs-info".
    /// </summary>
    public async Task<string> SyncTriageLabelsAsync(IssueReference issue, bool isClear, string? size)
    {
        EnsureAllowed(issue);

        if (isClear && string.IsNullOrWhiteSpace(size))
        {
            throw new ArgumentException("A size is required when the issue is clear.", nameof(size));
        }

        var wanted = isClear ? new[] { ReadyLabel, SizeLabelPrefix + size } : [NeedsInfoLabel];

        try
        {
            var gitHub = await clientFactory.GetClientForRepositoryAsync(issue.Owner, issue.Repo);
            var current = (await gitHub.Issue.Labels.GetAllForIssue(issue.Owner, issue.Repo, issue.Number))
                .Select(l => l.Name)
                .ToList();

            var toRemove = current
                .Where(IsManagedLabel)
                .Where(name => !wanted.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var toAdd = wanted
                .Where(name => !current.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            foreach (var name in toRemove)
            {
                await gitHub.Issue.Labels.RemoveFromIssue(issue.Owner, issue.Repo, issue.Number, name);
            }

            if (toAdd.Length > 0)
            {
                await gitHub.Issue.Labels.AddToIssue(issue.Owner, issue.Repo, issue.Number, toAdd);
            }

            var changes = toAdd.Select(n => "+" + n).Concat(toRemove.Select(n => "-" + n)).ToList();
            return changes.Count == 0 ? "unchanged" : string.Join(", ", changes);
        }
        catch (ApiException ex)
        {
            throw new GitHubIssueException($"GitHub returned {(int)ex.StatusCode} while updating the labels on {issue}: {ex.Message}", ex);
        }
    }

    private static bool IsManagedLabel(string name) =>
        string.Equals(name, ReadyLabel, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, NeedsInfoLabel, StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(SizeLabelPrefix, StringComparison.OrdinalIgnoreCase);

    private static string RenderComment(bool isClear, string? size, string rationale, IReadOnlyList<string>? missingInformation)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CommentMarker);
        sb.AppendLine("### 🤖 Issue triage");
        sb.AppendLine();

        if (isClear)
        {
            sb.AppendLine($"**Status:** ✅ Clear &nbsp;·&nbsp; **Size:** `{size}`");
            sb.AppendLine();
            sb.AppendLine($"**Assessment:** {rationale.Trim()}");
        }
        else
        {
            sb.AppendLine("**Status:** ❓ More information required");
            sb.AppendLine();
            sb.AppendLine($"**Assessment:** {rationale.Trim()}");
            sb.AppendLine();
            sb.AppendLine("**Please provide:**");
            foreach (var item in missingInformation ?? [])
            {
                sb.AppendLine($"- {item.Trim()}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("<sub>Sizes: XS = very small · S = small · L = large · XL = very large. Automated assessment by the issue triage agent.</sub>");
        return sb.ToString();
    }

    private static bool IsTriageComment(IssueComment comment) =>
        comment.Body?.Contains(CommentMarker, StringComparison.Ordinal) == true;

    private void EnsureAllowed(IssueReference issue) => RepositoryAccess.EnsureAllowed(issue, allowedRepositories);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "\n…(truncated)";
}

/// <summary>An expected GitHub failure (not allowed, not found, API error) with a user-facing message.</summary>
public sealed class GitHubIssueException(string message, Exception? innerException = null)
    : Exception(message, innerException);
