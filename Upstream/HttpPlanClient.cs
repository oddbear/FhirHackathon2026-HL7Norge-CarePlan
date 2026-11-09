using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;

namespace PlanerFhir.Upstream;

/// <summary>Calls the real Planer API (GET /plan, /plan/:id, /plan/:id/history, /plan/:id/version/:v).
/// The caller's Authorization header is forwarded as-is.</summary>
public sealed class HttpPlanClient(HttpClient http, IHttpContextAccessor accessor) : IPlanClient
{
    public async Task<IReadOnlyList<Plan>> SearchAsync(PlanQuery q, CancellationToken ct)
    {
        var qs = HttpUtility.ParseQueryString("");
        qs["patient"] = q.Patient;
        if (q.IsActive is { } a) qs["is_active"] = a ? "true" : "false";
        if (q.UpdatedSince is { } u) qs["updated_since"] = u.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        if (q.Published is { } p) qs["published"] = p ? "true" : "false";
        return await GetListAsync($"plan?{qs}", ct);
    }

    public Task<Plan?> GetAsync(string id, CancellationToken ct) =>
        GetOneAsync($"plan/{Uri.EscapeDataString(id)}", ct);

    public Task<Plan?> GetVersionAsync(string id, int version, CancellationToken ct) =>
        GetOneAsync($"plan/{Uri.EscapeDataString(id)}/version/{version}", ct);

    public async Task<IReadOnlyList<HistoryEvent>> GetHistoryAsync(string id, CancellationToken ct)
    {
        using var res = await SendAsync($"plan/{Uri.EscapeDataString(id)}/history", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return [];
        Ensure(res);
        return await res.Content.ReadFromJsonAsync<List<HistoryEvent>>(ct) ?? [];
    }

    private async Task<IReadOnlyList<Plan>> GetListAsync(string url, CancellationToken ct)
    {
        using var res = await SendAsync(url, ct);
        Ensure(res);
        return await res.Content.ReadFromJsonAsync<List<Plan>>(ct) ?? [];
    }

    private async Task<Plan?> GetOneAsync(string url, CancellationToken ct)
    {
        using var res = await SendAsync(url, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        Ensure(res);
        return await res.Content.ReadFromJsonAsync<Plan>(ct);
    }

    private Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        var auth = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth)) req.Headers.TryAddWithoutValidation("Authorization", auth);
        return http.SendAsync(req, ct);
    }

    private static void Ensure(HttpResponseMessage res)
    {
        if (!res.IsSuccessStatusCode)
            throw new UpstreamException($"Planer API returned {(int)res.StatusCode} {res.ReasonPhrase}", (int)res.StatusCode);
    }
}
