using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace PlanerFhir.Endpoints;

public static class FhirResults
{
    private static readonly FhirJsonSerializer Serializer = new();

    public static IResult Send(Resource resource, int status = 200, string? etag = null) =>
        new FhirHttpResult(Serializer.SerializeToString(resource, pretty: true), status, etag);

    public static IResult Outcome(int status, OperationOutcome.IssueType code, string message,
        OperationOutcome.IssueSeverity severity = OperationOutcome.IssueSeverity.Error) =>
        Send(new OperationOutcome
        {
            Issue = [new OperationOutcome.IssueComponent
            {
                Severity = severity, Code = code,
                Details = new CodeableConcept { Text = message },
            }],
        }, status);

    private sealed class FhirHttpResult(string json, int status, string? etag) : IResult
    {
        public async System.Threading.Tasks.Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/fhir+json; charset=utf-8";
            if (etag is not null) ctx.Response.Headers.ETag = $"W/\"{etag}\"";
            await ctx.Response.WriteAsync(json);
        }
    }
}
