using OnCallHelperLocal.Data;
using OnCallHelperLocal.Embeddings;

namespace OnCallHelperLocal;

/// <summary>`dotnet OnCallHelperLocal.dll setup [--wait]`: check the embedding service and create the vector index.</summary>
public static class Setup
{
    public static async Task<int> RunAsync(LocalSettings settings, bool wait)
    {
        var deadline = DateTime.UtcNow.AddMinutes(wait ? 20 : 1);   // the first start downloads the model
        using var http = new HttpClient
        {
            BaseAddress = new Uri(settings.EmbeddingsBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(120)
        };
        var embedder = new OllamaEmbedder(http, settings);
        var store = new IncidentStore(settings);

        while (true)
        {
            try
            {
                var vector = await embedder.EmbedQueryAsync("setup probe", CancellationToken.None);
                Console.WriteLine($"embedding service ok: model={settings.EmbeddingsModel}, dimensions={vector.Length}");
                await store.EnsureVectorIndexAsync(settings.EmbeddingsDimensions, waitUntilQueryable: true, CancellationToken.None);
                Console.WriteLine($"vector index '{IncidentStore.IndexName}' ready in database '{settings.MongoDatabase}'");
                return 0;
            }
            catch (Exception ex) when (DateTime.UtcNow < deadline)
            {
                Console.WriteLine($"waiting for services: {ex.Message}");
                await Task.Delay(5000);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"setup failed: {ex.Message}");
                return 1;
            }
        }
    }
}
