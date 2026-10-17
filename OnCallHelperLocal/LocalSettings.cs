namespace OnCallHelperLocal;

/// <summary>Settings come from environment variables; the defaults match compose.yaml.</summary>
public sealed record LocalSettings(
    string MongoConnectionString,
    string MongoDatabase,
    string EmbeddingsBaseUrl,
    string EmbeddingsModel,
    int EmbeddingsDimensions)
{
    public static LocalSettings FromEnvironment() => new(
        Env("MONGO_CONNECTION_STRING") ?? "mongodb://mongodb:27017/?directConnection=true",
        Env("MONGO_DATABASE") ?? "oncall_local",
        Env("EMBEDDINGS_BASE_URL") ?? "http://ollama:11434",
        Env("EMBEDDINGS_MODEL") ?? "nomic-embed-text",
        int.TryParse(Env("EMBEDDINGS_DIMENSIONS"), out var d) ? d : 768);

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}
