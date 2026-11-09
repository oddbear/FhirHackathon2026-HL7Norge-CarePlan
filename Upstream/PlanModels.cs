using System.Text.Json.Serialization;

namespace PlanerFhir.Upstream;

// Mirrors the JSON shapes in the Bruno collection "Planer API - dev nhn".

public sealed record Plan
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("revision")] public int Revision { get; init; }
    [JsonPropertyName("active_version")] public int? ActiveVersion { get; init; }
    [JsonPropertyName("is_active")] public bool? IsActive { get; init; }
    [JsonPropertyName("approvals")] public List<Approval>? Approvals { get; init; }
    [JsonPropertyName("clinical_content")] public ClinicalContent ClinicalContent { get; init; } = new();
    [JsonPropertyName("comment")] public string? Comment { get; init; }
    [JsonPropertyName("publication_status")] public List<Publication>? PublicationStatus { get; init; }
    [JsonPropertyName("subject")] public Subject Subject { get; init; } = new();
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("updated_by")] public Author? UpdatedBy { get; init; }
}

public sealed record ClinicalContent
{
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("personal_goals")] public string? PersonalGoals { get; init; }
    [JsonPropertyName("zones")] public List<Zone>? Zones { get; init; }
}

/// <summary>level is green | yellow | red.</summary>
public sealed record Zone
{
    [JsonPropertyName("level")] public string Level { get; init; } = "green";
    [JsonPropertyName("condition")] public string Condition { get; init; } = "";
    [JsonPropertyName("action")] public string Action { get; init; } = "";
}

public sealed record Approval
{
    [JsonPropertyName("status")] public string Status { get; init; } = "requested";
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record Publication
{
    [JsonPropertyName("provider_id")] public string ProviderId { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "published";
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record Subject
{
    [JsonPropertyName("nin")] public string Nin { get; init; } = "";
}

public sealed record Author
{
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("hpr_number")] public string? HprNumber { get; init; }
    [JsonPropertyName("nin")] public string? Nin { get; init; }
}

/// <summary>Entry from GET /plan/:id/history (an audit event, not a full plan version).</summary>
public sealed record HistoryEvent
{
    [JsonPropertyName("revision")] public int Revision { get; init; }
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("event_type")] public string EventType { get; init; } = "";
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("updated_by")] public Author? UpdatedBy { get; init; }
    [JsonPropertyName("comment")] public string? Comment { get; init; }
    [JsonPropertyName("approvals")] public int Approvals { get; init; }
    [JsonPropertyName("publications")] public int Publications { get; init; }
}

public sealed record PlanQuery(string Patient, bool? IsActive, DateTimeOffset? UpdatedSince, bool? Published);
