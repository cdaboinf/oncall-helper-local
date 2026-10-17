using System.Text;
using OnCallHelperLocal.Domain;

namespace OnCallHelperLocal.Services;

/// <summary>
/// The text that is embedded for an incident. Version 1 (the AWS app) embedded only the description, so a question about
/// HOW something was fixed could not match the fix. Version 2 adds the title, root cause and resolution summary.
/// </summary>
public static class EmbeddingText
{
    public const int CurrentVersion = 2;
    public const int MaxDescriptionChars = 4000;

    public static string For(string? title, string? description, string? rootCause, string? summary)
    {
        var sb = new StringBuilder();
        sb.Append(Clip(title, 300)).Append(". ").Append(Clip(description, MaxDescriptionChars));
        if (!string.IsNullOrWhiteSpace(rootCause)) sb.Append("\nRoot cause: ").Append(Clip(rootCause, 800));
        if (!string.IsNullOrWhiteSpace(summary)) sb.Append("\nResolution: ").Append(Clip(summary, 800));
        return sb.ToString();
    }

    public static string For(Incident i) =>
        For(i.Title, i.Metadata.Description, i.Resolution?.RootCause, i.Resolution?.Summary);

    private static string Clip(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length <= max ? t : t[..max];
    }
}
