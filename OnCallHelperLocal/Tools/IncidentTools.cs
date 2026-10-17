using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using OnCallHelperLocal.Services;

namespace OnCallHelperLocal.Tools;

[McpServerToolType]
public sealed class IncidentTools(IncidentService service)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    [McpServerTool(Name = "find_similar_incidents", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Semantic search over saved incidents, by meaning. The searchable text of each incident is its title, description, root cause and resolution summary, so you can search for what happened OR for how something was fixed. Pass a short summary or a plain-words description of the cause or fix, NOT a raw chat. Search 2-3 times with different phrasings and merge the results. Scores are relative similarity (about 0.5 to 1) and bunch together when incidents are alike: judge by the gap between the best result and the rest, and by reading the incidents.")]
    public Task<string> FindSimilarIncidents(
        [Description("A summary of the problem, a few sentences.")] string description,
        [Description("How many results, 1-20.")] int top = 5,
        CancellationToken ct = default) =>
        Run(async () => await service.FindSimilarAsync(description, top, ct));

    [McpServerTool(Name = "search_incidents", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Keyword and filter search over saved incidents, newest first. All filters are optional. The text query is a plain substring match on title, description and service name, so \"timeout\" does not match \"timed out\" and an empty keywordMatches list NEVER means nothing similar exists. When you give a text query, the response also carries semanticMatches (the closest incidents by meaning, with relative scores): read both. For any \"have we seen this / known issue / how was it resolved\" question, also call find_similar_incidents with a plain-words description of the problem.")]
    public Task<string> SearchIncidents(
        [Description("Words to look for. Matched as exact text against title, description and service name; the same text is also searched by meaning.")] string? query = null,
        [Description("Exact service name.")] string? serviceName = null,
        [Description("Severity as stored, e.g. sev2.")] string? severity = null,
        [Description("Environment as stored, e.g. production.")] string? environment = null,
        [Description("Only incidents created on or after this ISO-8601 date, e.g. 2026-10-01.")] string? from = null,
        [Description("Only incidents created on or before this ISO-8601 date.")] string? to = null,
        [Description("Max results, 1-100.")] int limit = 10,
        CancellationToken ct = default) =>
        Run(async () => await service.SearchAsync(query, serviceName, severity, environment, ParseDate(from, "from"), ParseDate(to, "to"), limit, ct));

    [McpServerTool(Name = "create_incident", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("WRITES a new incident. Only call after the user has approved the exact content, and after find_similar_incidents showed no close match. The description must be a short summary (what happened, symptoms, impact, when it started). Never put source code, configuration contents, copied wiki text, secrets, or customer personal data in any field; links and identifiers are fine. Leave the resolution fields out while the incident is still open.")]
    public Task<string> CreateIncident(
        [Description("Short, searchable title.")] string title,
        [Description("Summary: what happened, symptoms, impact, when it started.")] string description,
        [Description("Affected service.")] string serviceName,
        [Description("Normalized: production | staging | development | test, or empty.")] string environment,
        [Description("Normalized: sev1 | sev2 | sev3 | sev4, or empty. Never guess.")] string severity,
        [Description("Root cause, once known.")] string? rootCause = null,
        [Description("How it was resolved, once resolved.")] string? resolutionSummary = null,
        [Description("Concrete actions that were taken, in order.")] string[]? stepsTaken = null,
        [Description("Who resolved it.")] string? resolvedBy = null,
        CancellationToken ct = default) =>
        Run(async () => await service.CreateAsync(new NewIncident(title, description, serviceName, environment, severity,
            new ResolutionInput(rootCause, resolutionSummary, stepsTaken?.ToList(), resolvedBy)), ct));

    [McpServerTool(Name = "update_incident", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("CHANGES an existing incident. Fields you leave out keep their current values. Typical use: add the root cause and steps after the incident is resolved. Only call after the user has approved the exact change. The same content rules as create_incident apply: no code, copied wiki text, secrets, or personal data.")]
    public Task<string> UpdateIncident(
        [Description("Incident id (from a search or create result).")] string id,
        string? title = null,
        string? description = null,
        string? serviceName = null,
        string? environment = null,
        string? severity = null,
        string? rootCause = null,
        string? resolutionSummary = null,
        string[]? stepsTaken = null,
        string? resolvedBy = null,
        CancellationToken ct = default) =>
        Run(async () => await service.UpdateAsync(id, new IncidentPatch(title, description, serviceName, environment, severity,
            new ResolutionInput(rootCause, resolutionSummary, stepsTaken?.ToList(), resolvedBy)), ct));

    private static DateTime? ParseDate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)) return d;
        throw new McpException($"'{name}' is not a valid ISO-8601 date: {value}");
    }

    // Turn database or other failures into a readable tool error instead of a generic one.
    private static async Task<string> Run<T>(Func<Task<T>> action)
    {
        try { return JsonSerializer.Serialize(await action(), Json); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException($"{ex.GetType().Name}: {ex.Message}"); }
    }
}
