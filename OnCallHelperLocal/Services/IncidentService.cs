using ModelContextProtocol;
using OnCallHelperLocal.Data;
using OnCallHelperLocal.Domain;
using OnCallHelperLocal.Embeddings;

namespace OnCallHelperLocal.Services;

public sealed record ResolutionInput(string? RootCause, string? Summary, List<string>? StepsTaken, string? ResolvedBy);

public sealed record NewIncident(string Title, string Description, string ServiceName, string Environment, string Severity,
    ResolutionInput? Resolution);

/// <summary>Null means "keep the current value". An empty list for StepsTaken clears the steps.</summary>
public sealed record IncidentPatch(string? Title, string? Description, string? ServiceName, string? Environment,
    string? Severity, ResolutionInput? Resolution);

public sealed record ResolutionView(string? RootCause, string? Summary, List<string> StepsTaken, string? ResolvedBy);

public sealed record IncidentView(string Id, string Title, string ServiceName, string Environment, string Severity,
    string Description, DateTime CreatedAt, ResolutionView? Resolution, double? Score);

/// <summary>
/// Result of search_incidents. A keyword search only finds exact words, so an empty KeywordMatches never means "nothing
/// like this exists". When a text query is given, SemanticMatches (closest by meaning) always travel with it.
/// </summary>
public sealed record SearchResult(List<IncidentView> KeywordMatches, List<IncidentView> SemanticMatches, string Note);

public sealed record UpdateResult(IncidentView Incident, bool EmbeddingUpdated, string? Warning);

/// <summary>The rules mirror IncidentService in the AWS app: only the description is embedded.</summary>
public sealed class IncidentService(IncidentStore store, IEmbedder embedder)
{
    public const int MaxTitleChars = 300;
    // The embedding model reads a limited number of tokens, so a description must be a summary, not a raw chat.
    public const int MaxDescriptionChars = 6000;

    public async Task<IncidentView> CreateAsync(NewIncident n, CancellationToken ct)
    {
        Validate(n.Title, n.Description);
        var embedding = await embedder.EmbedDocumentAsync(
            EmbeddingText.For(n.Title, n.Description, n.Resolution?.RootCause, n.Resolution?.Summary), ct);

        var incident = new Incident
        {
            Title = n.Title.Trim(),
            Metadata = new IncidentMetadata
            {
                ServiceName = (n.ServiceName ?? "").Trim(),
                Environment = (n.Environment ?? "").Trim(),
                Severity = (n.Severity ?? "").Trim(),
                Description = n.Description.Trim()
            },
            Resolution = ToDomain(n.Resolution),
            CreatedAt = DateTime.UtcNow,
            Embedding = embedding,
            EmbeddingVersion = EmbeddingText.CurrentVersion
        };
        await store.CreateAsync(incident, ct);
        return ToView(incident);
    }

    public async Task<UpdateResult> UpdateAsync(string id, IncidentPatch p, CancellationToken ct)
    {
        var incident = await store.GetByIdAsync(id, ct) ?? throw new McpException($"Incident '{id}' was not found.");

        var newDescription = p.Description?.Trim();
        Validate(p.Title ?? incident.Title, newDescription ?? incident.Metadata.Description);
        var textBefore = EmbeddingText.For(incident);   // what the stored vector was made from

        if (p.Title is not null) incident.Title = p.Title.Trim();
        if (p.ServiceName is not null) incident.Metadata.ServiceName = p.ServiceName.Trim();
        if (p.Environment is not null) incident.Metadata.Environment = p.Environment.Trim();
        if (p.Severity is not null) incident.Metadata.Severity = p.Severity.Trim();
        if (newDescription is not null) incident.Metadata.Description = newDescription;

        if (p.Resolution is not null)
        {
            var current = incident.Resolution ?? new IncidentResolution();
            incident.Resolution = new IncidentResolution
            {
                RootCause = p.Resolution.RootCause ?? current.RootCause,
                Summary = p.Resolution.Summary ?? current.Summary,
                StepsTaken = p.Resolution.StepsTaken ?? current.StepsTaken,
                ResolvedBy = p.Resolution.ResolvedBy ?? current.ResolvedBy
            };
        }

        // Re-embed only when the embedded text (title, description, root cause, resolution summary) changed. If the
        // embedding service is down the edit still saves, but say so, because search would keep matching the old text.
        string? warning = null;
        var embeddingUpdated = false;
        if (EmbeddingText.For(incident) != textBefore)
        {
            try
            {
                incident.Embedding = await embedder.EmbedDocumentAsync(EmbeddingText.For(incident), ct);
                incident.EmbeddingVersion = EmbeddingText.CurrentVersion;
                embeddingUpdated = true;
            }
            catch (McpException ex)
            {
                warning = "The searchable text changed but the embedding could not be refreshed, so similarity search still " +
                          $"uses the old text. Fix the embedding service and save the change again. ({ex.Message})";
            }
        }

        await store.UpdateAsync(incident, ct);
        return new UpdateResult(ToView(incident), embeddingUpdated, warning);
    }

    private const int SemanticTop = 5;

    public async Task<SearchResult> SearchAsync(string? q, string? serviceName, string? severity, string? environment,
        DateTime? from, DateTime? to, int limit, CancellationToken ct)
    {
        var keyword = (await store.SearchAsync(q, serviceName, severity, environment, from, to, Math.Clamp(limit, 1, 100), ct))
            .Select(ToView).ToList();
        var semantic = new List<IncidentView>();
        string note;

        if (string.IsNullOrWhiteSpace(q))
        {
            note = "Filter-only search: there was no text query, so no semantic matches were computed.";
        }
        else if (q.Length > MaxDescriptionChars)
        {
            note = $"The query is longer than {MaxDescriptionChars} characters, so no semantic matches were computed; summarize it.";
        }
        else
        {
            try
            {
                var filtered = !string.IsNullOrWhiteSpace(serviceName) || !string.IsNullOrWhiteSpace(severity)
                    || !string.IsNullOrWhiteSpace(environment) || from.HasValue || to.HasValue;
                var vector = await embedder.EmbedQueryAsync(q, ct);
                // The vector search cannot take filters, so fetch more candidates and filter them here.
                var candidates = await store.FindSimilarAsync(vector, filtered ? 20 : SemanticTop, ct);
                semantic = candidates
                    .Where(i => Matches(i, serviceName, severity, environment, from, to))
                    .Take(SemanticTop).Select(ToView).ToList();
                note = "keywordMatches are incidents containing your exact words: an empty list does NOT mean nothing similar " +
                       "exists. semanticMatches are the closest incidents by meaning, with relative similarity scores (about 0.5 to 1): " +
                       "judge by the gap between the best and the rest, and read them.";
            }
            catch (McpException ex)
            {
                note = $"Semantic matches are unavailable ({ex.Message}); only keyword matches are shown. " +
                       "Run find_similar_incidents once the problem is fixed.";
            }
        }
        return new SearchResult(keyword, semantic, note);
    }

    private static bool Matches(Incident i, string? serviceName, string? severity, string? environment, DateTime? from, DateTime? to) =>
        (string.IsNullOrWhiteSpace(serviceName) || i.Metadata.ServiceName == serviceName.Trim())
        && (string.IsNullOrWhiteSpace(severity) || i.Metadata.Severity == severity.Trim())
        && (string.IsNullOrWhiteSpace(environment) || i.Metadata.Environment == environment.Trim())
        && (!from.HasValue || i.CreatedAt >= from.Value)
        && (!to.HasValue || i.CreatedAt <= to.Value);

    public async Task<List<IncidentView>> FindSimilarAsync(string description, int top, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(description)) throw new McpException("description is required.");
        if (description.Length > MaxDescriptionChars)
            throw new McpException($"description is {description.Length} characters; summarize it to under {MaxDescriptionChars} (a few sentences is enough).");
        var query = await embedder.EmbedQueryAsync(description, ct);
        return (await store.FindSimilarAsync(query, Math.Clamp(top, 1, 20), ct)).Select(ToView).ToList();
    }

    private static void Validate(string title, string description)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new McpException("title is required.");
        if (title.Length > MaxTitleChars) throw new McpException($"title is too long ({title.Length} characters, max {MaxTitleChars}).");
        if (string.IsNullOrWhiteSpace(description)) throw new McpException("description is required.");
        if (description.Length > MaxDescriptionChars)
            throw new McpException($"description is {description.Length} characters; summarize it to under {MaxDescriptionChars} (what happened, symptoms, impact, when it started).");
    }

    private static IncidentResolution? ToDomain(ResolutionInput? r) => r is null ? null : new IncidentResolution
    {
        RootCause = r.RootCause ?? "",
        Summary = r.Summary ?? "",
        StepsTaken = r.StepsTaken ?? [],
        ResolvedBy = r.ResolvedBy ?? ""
    };

    private static IncidentView ToView(Incident i) => new(
        i.Id, i.Title, i.Metadata.ServiceName, i.Metadata.Environment, i.Metadata.Severity, i.Metadata.Description,
        i.CreatedAt,
        i.Resolution is null ? null : new ResolutionView(i.Resolution.RootCause, i.Resolution.Summary, i.Resolution.StepsTaken ?? [], i.Resolution.ResolvedBy),
        i.Score);
}
