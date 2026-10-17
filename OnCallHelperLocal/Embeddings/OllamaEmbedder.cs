using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol;

namespace OnCallHelperLocal.Embeddings;

public interface IEmbedder
{
    /// <summary>Embed text that will be stored (an incident description).</summary>
    Task<float[]> EmbedDocumentAsync(string text, CancellationToken ct);

    /// <summary>Embed text used to search (a description of a new problem).</summary>
    Task<float[]> EmbedQueryAsync(string text, CancellationToken ct);
}

/// <summary>
/// Calls a local Ollama server (/api/embed). nomic-embed-text was trained with task prefixes, so stored text and
/// search text get different ones; other models use none. Keep this class small so the server can be swapped.
/// </summary>
public sealed class OllamaEmbedder(HttpClient http, LocalSettings settings) : IEmbedder
{
    private bool UsesNomicPrefixes => settings.EmbeddingsModel.StartsWith("nomic-embed-text", StringComparison.OrdinalIgnoreCase);

    public Task<float[]> EmbedDocumentAsync(string text, CancellationToken ct) =>
        EmbedAsync((UsesNomicPrefixes ? "search_document: " : "") + text, ct);

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken ct) =>
        EmbedAsync((UsesNomicPrefixes ? "search_query: " : "") + text, ct);

    private async Task<float[]> EmbedAsync(string input, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("api/embed", new { model = settings.EmbeddingsModel, input = new[] { input } }, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new McpException($"The embedding service is not reachable at {http.BaseAddress}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException("The embedding service timed out (the model may still be loading; try again).");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var hint = response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? $" The model '{settings.EmbeddingsModel}' may not be downloaded yet (docker exec oncall-local-ollama ollama pull {settings.EmbeddingsModel})."
                    : "";
                throw new McpException($"The embedding service returned {(int)response.StatusCode}: {Truncate(body)}{hint}");
            }

            using var doc = JsonDocument.Parse(body);
            var first = doc.RootElement.GetProperty("embeddings")[0];
            var vector = first.EnumerateArray().Select(e => e.GetSingle()).ToArray();
            if (vector.Length != settings.EmbeddingsDimensions)
                throw new McpException($"The model returns {vector.Length} dimensions but EMBEDDINGS_DIMENSIONS is {settings.EmbeddingsDimensions}.");
            return vector;
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
