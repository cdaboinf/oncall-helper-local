using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace OnCallHelperLocal.Domain;

// Same document shape as the AWS app (OnCallHelperApi.Domain), so incidents could move between them later.
public class Incident
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public IncidentMetadata Metadata { get; set; } = new();

    public IncidentResolution? Resolution { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public float[]? Embedding { get; set; }
    public int EmbeddingVersion { get; set; } = 1;

    // Filled by $vectorSearch (vectorSearchScore); never stored.
    [BsonElement("score")]
    [BsonIgnoreIfNull]
    public double? Score { get; set; }
}

public class IncidentMetadata
{
    public string ServiceName { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
}

public class IncidentResolution
{
    public string? Summary { get; set; }
    public List<string>? StepsTaken { get; set; }
    public string? RootCause { get; set; }
    public string? ResolvedBy { get; set; }
}
