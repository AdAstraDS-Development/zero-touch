namespace GitHubTools;

/// <summary>Checks that a repository may be accessed: valid owner/repo names and the allowlist.</summary>
internal static class RepositoryAccess
{
    public static void EnsureAllowed(IssueReference issue, string[] allowedRepositories)
    {
        // Owner and repo end up in the request path.
        if (!IsValidName(issue.Owner) || !IsValidName(issue.Repo))
        {
            throw new GitHubIssueException("Owner and repo must only contain letters, digits, '-', '_' and '.'.");
        }

        if (allowedRepositories.Length == 0)
        {
            return;
        }

        var allowed = allowedRepositories.Any(entry =>
            string.Equals(entry, $"{issue.Owner}/{issue.Repo}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry, $"{issue.Owner}/*", StringComparison.OrdinalIgnoreCase));

        if (!allowed)
        {
            throw new GitHubIssueException($"Repository {issue.Owner}/{issue.Repo} is not in the allowed list for this agent.");
        }
    }

    private static bool IsValidName(string value) =>
        value.Length is > 0 and <= 100 && value is not "." and not ".." &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
