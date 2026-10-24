using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace GitHubTools;

/// <summary>A GitHub issue, identified by owner, repository and number.</summary>
public sealed partial record IssueReference(string Owner, string Repo, int Number)
{
    public override string ToString() => $"{Owner}/{Repo}#{Number}";

    /// <summary>
    /// Finds the first issue reference in free text. Accepts
    /// "https://&lt;github-host&gt;/&lt;owner&gt;/&lt;repo&gt;/issues/&lt;number&gt;" (including GitHub Enterprise)
    /// and "&lt;owner&gt;/&lt;repo&gt;#&lt;number&gt;".
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out IssueReference? reference)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = ReferencePattern().Match(text);
        if (!match.Success || !int.TryParse(match.Groups["number"].ValueSpan, out var number) || number <= 0)
        {
            return false;
        }

        reference = new IssueReference(match.Groups["owner"].Value, match.Groups["repo"].Value, number);
        return true;
    }

    [GeneratedRegex(
        @"https?://[^/\s]+/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/issues/(?<number>\d+)" +
        @"|(?<![A-Za-z0-9_./-])(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)#(?<number>\d+)")]
    private static partial Regex ReferencePattern();
}
