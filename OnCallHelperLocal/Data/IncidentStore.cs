using System.Text.RegularExpressions;
using ModelContextProtocol;
using MongoDB.Bson;
using MongoDB.Driver;
using OnCallHelperLocal.Domain;

namespace OnCallHelperLocal.Data;

/// <summary>MongoDB access. The queries mirror IncidentRepository in the AWS app.</summary>
public sealed class IncidentStore
{
    public const string IndexName = "vector_index";   // the name the AWS app also uses

    private readonly IMongoDatabase _db;
    private readonly IMongoCollection<Incident> _incidents;
    private volatile bool _indexVerified;

    public IncidentStore(LocalSettings settings)
    {
        var client = new MongoClient(settings.MongoConnectionString);
        _db = client.GetDatabase(settings.MongoDatabase);
        _incidents = _db.GetCollection<Incident>("incidents");
    }

    public Task CreateAsync(Incident incident, CancellationToken ct) =>
        _incidents.InsertOneAsync(incident, cancellationToken: ct);

    /// <summary>Insert or replace by id. Used by `import`, so re-running it never duplicates.</summary>
    public Task UpsertAsync(Incident incident, CancellationToken ct) =>
        _incidents.ReplaceOneAsync(x => x.Id == incident.Id, incident, new ReplaceOptions { IsUpsert = true }, ct);

    public Task UpdateAsync(Incident incident, CancellationToken ct) =>
        _incidents.ReplaceOneAsync(x => x.Id == incident.Id, incident, cancellationToken: ct);

    public Task<List<Incident>> GetAllAsync(CancellationToken ct) => _incidents.Find(_ => true).ToListAsync(ct);

    public async Task<Incident?> GetByIdAsync(string id, CancellationToken ct) =>
        ObjectId.TryParse(id, out _) ? await _incidents.Find(x => x.Id == id).FirstOrDefaultAsync(ct) : null;

    public async Task<List<Incident>> SearchAsync(string? q, string? serviceName, string? severity, string? environment,
        DateTime? from, DateTime? to, int limit, CancellationToken ct)
    {
        var fb = Builders<Incident>.Filter;
        var filters = new List<FilterDefinition<Incident>>();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var regex = new BsonRegularExpression(Regex.Escape(q.Trim()), "i");
            filters.Add(fb.Or(
                fb.Regex(x => x.Title, regex),
                fb.Regex("Metadata.Description", regex),
                fb.Regex("Metadata.ServiceName", regex)));
        }
        if (!string.IsNullOrWhiteSpace(serviceName)) filters.Add(fb.Eq("Metadata.ServiceName", serviceName.Trim()));
        if (!string.IsNullOrWhiteSpace(severity)) filters.Add(fb.Eq("Metadata.Severity", severity.Trim()));
        if (!string.IsNullOrWhiteSpace(environment)) filters.Add(fb.Eq("Metadata.Environment", environment.Trim()));
        if (from.HasValue) filters.Add(fb.Gte(x => x.CreatedAt, from.Value));
        if (to.HasValue) filters.Add(fb.Lte(x => x.CreatedAt, to.Value));

        var filter = filters.Count > 0 ? fb.And(filters) : fb.Empty;
        return await _incidents.Find(filter)
            .SortByDescending(x => x.CreatedAt)
            .Limit(limit)
            .ToListAsync(ct);
    }

    public async Task<List<Incident>> FindSimilarAsync(float[] embedding, int top, CancellationToken ct)
    {
        await EnsureIndexPresentAsync(ct);

        var pipeline = new[]
        {
            new BsonDocument("$vectorSearch", new BsonDocument
            {
                { "index", IndexName },
                { "path", "Embedding" },
                { "queryVector", new BsonArray(embedding.Select(f => (double)f)) },
                { "numCandidates", Math.Max(50, top * 10) },
                { "limit", top }
            }),
            new BsonDocument("$addFields", new BsonDocument("score", new BsonDocument("$meta", "vectorSearchScore")))
        };
        return await _incidents.Aggregate<Incident>(pipeline).ToListAsync(ct);
    }

    /// <summary>Creates the vector index if it is missing. Run by `setup`; safe to repeat.</summary>
    public async Task EnsureVectorIndexAsync(int dimensions, bool waitUntilQueryable, CancellationToken ct)
    {
        var names = await (await _db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
        if (!names.Contains("incidents")) await _db.CreateCollectionAsync("incidents", cancellationToken: ct);

        var existing = await FindIndexAsync(ct);
        if (existing is null)
        {
            var definition = new BsonDocument("fields", new BsonArray
            {
                new BsonDocument { { "type", "vector" }, { "path", "Embedding" }, { "numDimensions", dimensions }, { "similarity", "cosine" } }
            });
            await _incidents.SearchIndexes.CreateOneAsync(new CreateSearchIndexModel(IndexName, SearchIndexType.VectorSearch, definition), ct);
        }
        else
        {
            var dims = existing.GetValue("latestDefinition", new BsonDocument()).AsBsonDocument
                .GetValue("fields", new BsonArray()).AsBsonArray
                .Select(f => f.AsBsonDocument.GetValue("numDimensions", 0).ToInt32()).FirstOrDefault();
            if (dims != dimensions)
                throw new InvalidOperationException($"The existing vector index has {dims} dimensions but the model uses {dimensions}. " +
                    "A different embedding model needs a new index and re-embedded incidents.");
        }

        if (waitUntilQueryable)
        {
            for (var i = 0; i < 60; i++)
            {
                var idx = await FindIndexAsync(ct);
                if (idx is not null && idx.GetValue("queryable", false).ToBoolean()) { _indexVerified = true; return; }
                await Task.Delay(2000, ct);
            }
            throw new InvalidOperationException("The vector index was created but is not queryable yet.");
        }
        _indexVerified = true;
    }

    // Atlas silently returns an empty list when the index is missing, so check before searching.
    private async Task EnsureIndexPresentAsync(CancellationToken ct)
    {
        if (_indexVerified) return;
        var idx = await FindIndexAsync(ct);
        if (idx is null || !idx.GetValue("queryable", false).ToBoolean())
            throw new McpException("The vector index is missing or not ready. Run: docker exec oncall-local-mcp dotnet OnCallHelperLocal.dll setup");
        _indexVerified = true;
    }

    private async Task<BsonDocument?> FindIndexAsync(CancellationToken ct)
    {
        using var cursor = await _incidents.SearchIndexes.ListAsync(IndexName, null, ct);
        return (await cursor.ToListAsync(ct)).FirstOrDefault();
    }
}
