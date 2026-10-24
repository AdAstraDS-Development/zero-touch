using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Octokit;

namespace GitHubTools;

/// <summary>
/// Read-only access to the source code of a repository: pins the head commit of the default branch
/// and downloads the code at that commit as one tarball into a <see cref="RepositorySnapshot"/>, so
/// the triage agent's tools work locally instead of making one GitHub request per call.
/// </summary>
public sealed partial class GitHubCodeService(GitHubAppClientFactory clientFactory, string[] allowedRepositories)
{
    /// <summary>Parent directory of all snapshots (one subdirectory per triage).</summary>
    public static readonly string SnapshotDirectory = Path.Combine(Path.GetTempPath(), "issue-triage");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Returns the default branch and the SHA of its head commit.</summary>
    public async Task<(string Branch, string CommitSha)> GetDefaultBranchHeadAsync(IssueReference issue)
    {
        RepositoryAccess.EnsureAllowed(issue, allowedRepositories);

        try
        {
            var gitHub = await clientFactory.GetClientForRepositoryAsync(issue.Owner, issue.Repo);
            var repository = await gitHub.Repository.Get(issue.Owner, issue.Repo);
            var branch = await gitHub.Repository.Branch.Get(issue.Owner, issue.Repo, repository.DefaultBranch);
            return (repository.DefaultBranch, branch.Commit.Sha);
        }
        catch (ApiException ex)
        {
            throw new GitHubIssueException($"GitHub returned {(int)ex.StatusCode} while reading the default branch of {issue.Owner}/{issue.Repo}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Downloads the repository's code at <paramref name="commitSha"/> (GitHub tarball, one request)
    /// into a new directory below <see cref="SnapshotDirectory"/>. The caller disposes the snapshot.
    /// </summary>
    public async Task<RepositorySnapshot> DownloadSnapshotAsync(
        IssueReference issue, string commitSha, CancellationToken cancellationToken = default)
    {
        RepositoryAccess.EnsureAllowed(issue, allowedRepositories);
        if (!CommitShaPattern().IsMatch(commitSha))
        {
            throw new GitHubIssueException($"'{commitSha}' is not a commit SHA.");
        }

        try
        {
            var token = await clientFactory.GetInstallationTokenAsync(issue.Owner, issue.Repo);
            var url = $"{clientFactory.ApiBaseAddress.AbsoluteUri.TrimEnd('/')}/repos/{issue.Owner}/{issue.Repo}/tarball/{commitSha}";

            // GitHub redirects to a short-lived download URL; HttpClient drops the Authorization header on the redirect.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("zero-touch-issue-triage");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new GitHubIssueException($"GitHub returned {(int)response.StatusCode} while downloading the code of {issue.Owner}/{issue.Repo}.");
            }

            await using var tarGz = await response.Content.ReadAsStreamAsync(cancellationToken);
            var root = Path.Combine(SnapshotDirectory, Guid.NewGuid().ToString("N"));
            return await RepositorySnapshot.ExtractAsync(tarGz, root, cancellationToken);
        }
        catch (ApiException ex)
        {
            throw new GitHubIssueException($"GitHub returned {(int)ex.StatusCode} for {issue.Owner}/{issue.Repo}: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new GitHubIssueException($"Downloading the code of {issue.Owner}/{issue.Repo} failed: {ex.Message}", ex);
        }
    }

    /// <summary>Deletes snapshots left behind by runs that ended abnormally (e.g. a restarted container).</summary>
    public static void DeleteStaleSnapshots(TimeSpan maxAge)
    {
        if (!Directory.Exists(SnapshotDirectory))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(SnapshotDirectory))
        {
            try
            {
                if (Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow - maxAge)
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or already gone; try again next time.
            }
        }
    }

    [GeneratedRegex("^[0-9a-f]{40}([0-9a-f]{24})?$")]
    private static partial Regex CommitShaPattern();
}
