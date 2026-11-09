namespace PlanerFhir.Upstream;

public interface IPlanClient
{
    Task<IReadOnlyList<Plan>> SearchAsync(PlanQuery query, CancellationToken ct);
    Task<Plan?> GetAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<HistoryEvent>> GetHistoryAsync(string id, CancellationToken ct);
    Task<Plan?> GetVersionAsync(string id, int version, CancellationToken ct);

    /// <summary>POST /plan/:id/{command}; command is "$activate", "$approval-given" or "$mark-published".</summary>
    Task<CommandResult> ExecuteAsync(string id, string command, CommandRequest request, CancellationToken ct);
}

/// <summary>Body of the upstream state-change commands. Version is only sent when set.</summary>
public sealed record CommandRequest(string? Comment, int Revision, int? Version);

/// <summary>Upstream response: the plan id and its new revision.</summary>
public sealed record CommandResult(string Id, int Revision);

public static class PlanCommands
{
    public const string Activate = "$activate";
    public const string Approve = "$approval-given";
    public const string Publish = "$mark-published";
}

public sealed class UpstreamException(string message, int? status = null, string? code = null) : Exception(message)
{
    public int? Status { get; } = status;
    /// <summary>Upstream error code from the {code, details, error} body, if any.</summary>
    public string? Code { get; } = code;
}
