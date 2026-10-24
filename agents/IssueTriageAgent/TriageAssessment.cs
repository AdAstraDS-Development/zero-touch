using System.ComponentModel;

namespace IssueTriageAgent;

/// <summary>The structured result of the issue triage agent.</summary>
public sealed record TriageAssessment(
    [property: Description("true if the issue is clear enough to start working on it without further questions")]
    bool IsClear,
    [property: Description("T-shirt size: XS, S, L or XL when IsClear is true; null otherwise.")]
    string? Size,
    [property: Description("Short justification of the assessment and the size (2-5 sentences, markdown allowed).")]
    string Rationale,
    [property: Description("Concrete questions for the author about missing or ambiguous information when IsClear is false; null otherwise.")]
    string[]? MissingInformation)
{
    public static readonly string[] AllowedSizes = ["XS", "S", "L", "XL"];

    /// <summary>Returns a copy with the size normalized (trimmed, upper case, dropped when not clear).</summary>
    public TriageAssessment Normalize() => this with
    {
        Size = IsClear ? Size?.Trim().ToUpperInvariant() : null,
        MissingInformation = IsClear ? null : MissingInformation,
    };

    /// <summary>Returns an error message if the assessment is inconsistent, otherwise null.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Rationale))
        {
            return "Rationale must not be empty.";
        }

        if (IsClear && (Size is null || !AllowedSizes.Contains(Size)))
        {
            return $"Size must be one of {string.Join(", ", AllowedSizes)} when IsClear is true.";
        }

        if (!IsClear && (MissingInformation is null || MissingInformation.Length == 0))
        {
            return "MissingInformation must list at least one question when IsClear is false.";
        }

        return null;
    }
}
