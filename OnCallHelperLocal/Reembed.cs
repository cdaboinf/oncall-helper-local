using OnCallHelperLocal.Data;
using OnCallHelperLocal.Embeddings;
using OnCallHelperLocal.Services;

namespace OnCallHelperLocal;

/// <summary>
/// `reembed [--dry-run] [--all]`: recompute the vectors of incidents saved with an older embedding recipe (or all of them),
/// from the text already stored. Needs no export file. Safe to repeat.
/// </summary>
public static class Reembed
{
    public static async Task<int> RunAsync(LocalSettings settings, string[] args)
    {
        var dryRun = args.Contains("--dry-run");
        var all = args.Contains("--all");

        using var http = new HttpClient
        {
            BaseAddress = new Uri(settings.EmbeddingsBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(120)
        };
        var embedder = new OllamaEmbedder(http, settings);
        var store = new IncidentStore(settings);

        var incidents = await store.GetAllAsync(CancellationToken.None);
        var todo = incidents.Where(i => all || i.EmbeddingVersion != EmbeddingText.CurrentVersion).ToList();
        Console.WriteLine($"{incidents.Count} incidents in '{settings.MongoDatabase}'; {todo.Count} need the version {EmbeddingText.CurrentVersion} embedding{(all ? " (--all)" : "")}.");
        if (dryRun || todo.Count == 0) return 0;

        int done = 0, failed = 0;
        foreach (var incident in todo)
        {
            if (string.IsNullOrWhiteSpace(incident.Metadata.Description)) { failed++; Console.Error.WriteLine($"incident {incident.Id} has no description; skipped"); continue; }
            try
            {
                incident.Embedding = await embedder.EmbedDocumentAsync(EmbeddingText.For(incident), CancellationToken.None);
                incident.EmbeddingVersion = EmbeddingText.CurrentVersion;
                await store.UpdateAsync(incident, CancellationToken.None);
                done++;
            }
            catch (Exception ex) { failed++; Console.Error.WriteLine($"incident {incident.Id} failed: {ex.Message}"); }
        }
        Console.WriteLine($"re-embedded {done}; failed or skipped {failed}.");
        return failed > 0 ? 1 : 0;
    }
}
