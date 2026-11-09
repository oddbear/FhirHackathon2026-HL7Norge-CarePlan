namespace PlanerFhir.Upstream;

public interface IPlanClient
{
    Task<IReadOnlyList<Plan>> SearchAsync(PlanQuery query, CancellationToken ct);
    Task<Plan?> GetAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<HistoryEvent>> GetHistoryAsync(string id, CancellationToken ct);
    Task<Plan?> GetVersionAsync(string id, int version, CancellationToken ct);
}

public sealed class UpstreamException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;
}
