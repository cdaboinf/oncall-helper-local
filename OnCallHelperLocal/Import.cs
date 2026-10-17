using System.Text.Json;
using OnCallHelperLocal.Data;
using OnCallHelperLocal.Domain;
using OnCallHelperLocal.Embeddings;
using OnCallHelperLocal.Services;
using MongoDB.Bson;

namespace OnCallHelperLocal;

/// <summary>
/// `import [--dry-run]`: reads a JSON array of incidents from stdin (the export of the hosted app) and loads them into the
/// local database. Original ids and dates are kept, so running it again updates instead of duplicating. The hosted app's
/// vectors come from a different model, so every description is embedded again here.
/// </summary>
public static class Import
{
    public static async Task<int> RunAsync(LocalSettings settings, string[] args)
    {
        var dryRun = args.Contains("--dry-run");

        List<IncidentView>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<IncidentView>>(await Console.In.ReadToEndAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"The input is not a JSON array of incidents: {ex.Message}");
            return 1;
        }
        if (items is null || items.Count == 0) { Console.Error.WriteLine("No incidents in the input."); return 1; }

        using var http = new HttpClient
        {
            BaseAddress = new Uri(settings.EmbeddingsBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(120)
        };
        var embedder = new OllamaEmbedder(http, settings);
        var store = new IncidentStore(settings);
        if (!dryRun) await store.EnsureVectorIndexAsync(settings.EmbeddingsDimensions, waitUntilQueryable: true, CancellationToken.None);

        int imported = 0, noDescription = 0, badId = 0, failed = 0, embeddedFromTruncated = 0, n = 0;
        foreach (var item in items)
        {
            n++;
            if (string.IsNullOrWhiteSpace(item.Description)) { noDescription++; continue; }
            if (!ObjectId.TryParse(item.Id, out _)) { badId++; continue; }

            // Store the full description; the embedded text (title, description, root cause, resolution summary) is capped.
            var text = EmbeddingText.For(item.Title, item.Description, item.Resolution?.RootCause, item.Resolution?.Summary);
            if (item.Description.Length > EmbeddingText.MaxDescriptionChars) embeddedFromTruncated++;
            if (dryRun) { imported++; continue; }

            try
            {
                var embedding = await embedder.EmbedDocumentAsync(text, CancellationToken.None);
                await store.UpsertAsync(new Incident
                {
                    Id = item.Id,
                    Title = item.Title ?? "",
                    Metadata = new IncidentMetadata
                    {
                        ServiceName = item.ServiceName ?? "",
                        Environment = item.Environment ?? "",
                        Severity = item.Severity ?? "",
                        Description = item.Description
                    },
                    Resolution = item.Resolution is null ? null : new IncidentResolution
                    {
                        RootCause = item.Resolution.RootCause,
                        Summary = item.Resolution.Summary,
                        StepsTaken = item.Resolution.StepsTaken ?? [],
                        ResolvedBy = item.Resolution.ResolvedBy
                    },
                    CreatedAt = DateTime.SpecifyKind(item.CreatedAt, DateTimeKind.Utc),
                    Embedding = embedding,
                    EmbeddingVersion = EmbeddingText.CurrentVersion
                }, CancellationToken.None);
                imported++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"incident {item.Id} failed: {ex.Message}");
            }
            if (n % 25 == 0) Console.WriteLine($"... {n}/{items.Count}");
        }

        Console.WriteLine($"{(dryRun ? "dry run: would import" : "imported")} {imported} of {items.Count} incidents into database '{settings.MongoDatabase}'");
        Console.WriteLine($"skipped: {noDescription} without a description, {badId} with an invalid id; failed: {failed}; descriptions longer than {EmbeddingText.MaxDescriptionChars} chars (embedded from the first {EmbeddingText.MaxDescriptionChars}): {embeddedFromTruncated}");
        return failed > 0 ? 1 : 0;
    }
}
