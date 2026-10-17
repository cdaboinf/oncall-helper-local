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

    public async Task<List<IncidentView>> SearchAsync(string? q, string? serviceName, string? severity, string? environment,
        DateTime? from, DateTime? to, int limit, CancellationToken ct) =>
        (await store.SearchAsync(q, serviceName, severity, environment, from, to, Math.Clamp(limit, 1, 100), ct))
            .Select(ToView).ToList();

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
