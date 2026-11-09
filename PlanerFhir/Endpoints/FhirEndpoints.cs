using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using PlanerFhir.Mapping;
using PlanerFhir.Upstream;
using static PlanerFhir.Endpoints.FhirResults;
using Task = System.Threading.Tasks.Task;

namespace PlanerFhir.Endpoints;

public static class FhirEndpoints
{
    // Resource types that can be searched, mapped from a PlanResources instance.
    private static readonly Dictionary<string, Func<PlanResources, IEnumerable<Resource>>> Searchable =
        new(StringComparer.Ordinal)
        {
            ["CarePlan"] = r => [r.CarePlan],
            ["Composition"] = r => [r.Composition],
            ["ServiceRequest"] = r => r.ServiceRequests,
            ["Task"] = r => r.Tasks,
            ["Provenance"] = r => r.Provenance is null ? [] : [r.Provenance],
        };

    private static readonly System.Text.Json.JsonSerializerOptions FhirJson =
        new System.Text.Json.JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);

    public static void MapFhir(this WebApplication app)
    {
        var g = app.MapGroup("/fhir");

        g.MapGet("/metadata", (HttpContext ctx) => Send(Capability(ctx)));

        // ---- PlanDefinition: static definition of the plan structure (no upstream call) ---------------
        g.MapGet("/PlanDefinition", (HttpContext ctx, PlanMapper mapper) =>
        {
            var def = mapper.MapPlanDefinition();
            var bundle = new Bundle { Type = Bundle.BundleType.Searchset, Total = 1 };
            bundle.Link.Add(new Bundle.LinkComponent { Relation = "self", Url = $"{BaseUrl(ctx)}/PlanDefinition" });
            bundle.Entry.Add(new Bundle.EntryComponent
            {
                FullUrl = $"{BaseUrl(ctx)}/PlanDefinition/{def.Id}",
                Resource = def,
                Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Match },
            });
            return Send(bundle);
        });

        g.MapGet("/PlanDefinition/{id}", (string id, PlanMapper mapper) =>
            id == PlanMapper.PlanDefinitionId
                ? Send(mapper.MapPlanDefinition())
                : Outcome(404, OperationOutcome.IssueType.NotFound, $"PlanDefinition/{id} not found"));

        // ---- search: GET /fhir/{type}?patient=...&status=...&_lastUpdated=...&published=... -------
        g.MapGet("/{type}", async (string type, HttpContext ctx, IPlanClient client, PlanMapper mapper, CancellationToken ct) =>
        {
            if (!Searchable.TryGetValue(type, out var select))
                return Outcome(404, OperationOutcome.IssueType.NotSupported, $"Resource type '{type}' is not supported. Supported: {string.Join(", ", Searchable.Keys)}.");

            var q = ctx.Request.Query;

            // Provenance?target=CarePlan/{id}: audit events of one plan from upstream /plan/:id/history.
            if (type == "Provenance" && q["target"].FirstOrDefault() is { Length: > 0 } target)
            {
                var planId = PlanMapper.PlanIdFrom(target[(target.LastIndexOf('/') + 1)..]);
                if (planId is null) return Outcome(400, OperationOutcome.IssueType.Invalid, "target must be CarePlan/{id} or a plan id.");
                try
                {
                    var events = (await client.GetHistoryAsync(planId, ct)).OrderByDescending(e => e.Revision).ToList();
                    var provBundle = new Bundle { Type = Bundle.BundleType.Searchset, Total = events.Count };
                    foreach (var e in events)
                    {
                        var pv = mapper.MapEvent(planId, e);
                        provBundle.Entry.Add(new Bundle.EntryComponent
                        {
                            FullUrl = $"{BaseUrl(ctx)}/Provenance/{pv.Id}", Resource = pv,
                            Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Match },
                        });
                    }
                    return Send(provBundle);
                }
                catch (UpstreamException e) { return Outcome(502, OperationOutcome.IssueType.Transient, e.Message); }
            }

            var nin = PatientParam(q);
            if (nin is null)
                return Outcome(400, OperationOutcome.IssueType.Required, "Search parameter 'patient' (national identity number) is required, e.g. ?patient=18679135941 or ?patient:identifier=urn:oid:2.16.578.1.12.4.1.4.1|18679135941.");

            bool? isActive = q["status"].FirstOrDefault() is { Length: > 0 } s ? s == "active" : null;
            bool? published = q["published"].FirstOrDefault() is { Length: > 0 } pb ? pb == "true" : null;

            DateTimeOffset? since = null;
            if (q["_lastUpdated"].FirstOrDefault() is { Length: > 0 } lu)
            {
                // Only the "ge"/"gt" prefixes make sense for upstream's updated_since.
                var raw = lu.StartsWith("ge") || lu.StartsWith("gt") ? lu[2..] : lu;
                if (!DateTimeOffset.TryParse(raw, out var parsed))
                    return Outcome(400, OperationOutcome.IssueType.Invalid, $"_lastUpdated '{lu}' is not a valid FHIR dateTime (use e.g. ge2026-09-01T00:00:00Z).");
                since = parsed;
            }

            int count = int.TryParse(q["_count"].FirstOrDefault(), out var c) ? Math.Clamp(c, 1, 200) : 50;
            int offset = int.TryParse(q["_offset"].FirstOrDefault(), out var off) ? Math.Max(off, 0) : 0;

            IReadOnlyList<Plan> plans;
            try { plans = await client.SearchAsync(new PlanQuery(nin, isActive, since, published), ct); }
            catch (UpstreamException e) { return Outcome(502, OperationOutcome.IssueType.Transient, e.Message); }

            // Upstream has no paging and no ordering guarantee: sort newest first so _offset/_count are stable.
            var mapped = plans.OrderByDescending(p => p.UpdatedAt).ThenBy(p => p.Id, StringComparer.Ordinal).Select(mapper.Map).ToList();
            var all = mapped.SelectMany(select).ToList();
            var matches = all.Skip(offset).Take(count).ToList();

            var bundle = new Bundle { Type = Bundle.BundleType.Searchset, Total = all.Count };
            bundle.Link.Add(new Bundle.LinkComponent { Relation = "self", Url = PageUrl(ctx, offset) });
            if (offset + count < all.Count)
                bundle.Link.Add(new Bundle.LinkComponent { Relation = "next", Url = PageUrl(ctx, offset + count) });
            var seen = new HashSet<string>();
            void Add(Resource r, Bundle.SearchEntryMode mode)
            {
                if (!seen.Add($"{r.TypeName}/{r.Id}")) return;
                bundle.Entry.Add(new Bundle.EntryComponent
                {
                    FullUrl = $"{BaseUrl(ctx)}/{r.TypeName}/{r.Id}",
                    Resource = r,
                    Search = new Bundle.SearchComponent { Mode = mode },
                });
            }

            foreach (var m in matches) Add(m, Bundle.SearchEntryMode.Match);

            // _include / _revinclude. Matching is by the plans that produced a match.
            var matchedKeys = matches.Select(m => $"{m.TypeName}/{m.Id}").ToHashSet();
            var involved = mapped.Where(r => r.All().Any(x => matchedKeys.Contains($"{x.TypeName}/{x.Id}"))).ToList();

            foreach (var inc in q["_include"])
                foreach (var r in involved)
                    switch (inc)
                    {
                        case "CarePlan:subject" or "CarePlan:patient" or "ServiceRequest:subject" or "Task:patient" or "Composition:subject":
                            Add(r.Patient, Bundle.SearchEntryMode.Include); break;
                        case "CarePlan:author" or "CarePlan:contributor" or "Composition:author" or "ServiceRequest:requester":
                            if (r.Practitioner is not null) Add(r.Practitioner, Bundle.SearchEntryMode.Include); break;
                        case "ServiceRequest:based-on":
                            Add(r.CarePlan, Bundle.SearchEntryMode.Include); break;
                    }

            foreach (var inc in q["_revinclude"])
                foreach (var r in involved)
                    switch (inc)
                    {
                        case "ServiceRequest:based-on":
                            foreach (var sr in r.ServiceRequests) Add(sr, Bundle.SearchEntryMode.Include); break;
                        case "Task:focus":
                            foreach (var t in r.Tasks) Add(t, Bundle.SearchEntryMode.Include); break;
                        case "Composition:entry":
                            Add(r.Composition, Bundle.SearchEntryMode.Include); break;
                        case "Provenance:target":
                            if (r.Provenance is not null) Add(r.Provenance, Bundle.SearchEntryMode.Include); break;
                    }

            return Send(bundle);
        });

        // ---- read: GET /fhir/{type}/{id} ----------------------------------------------------------
        g.MapGet("/{type}/{id}", async (string type, string id, IPlanClient client, PlanMapper mapper, CancellationToken ct) =>
        {
            var found = await Resolve(type, id, null, client, mapper, ct);
            return found.Result ?? Send(found.Resource!, etag: found.Resource!.Meta?.VersionId);
        });

        // ---- vread: GET /fhir/{type}/{id}/_history/{vid} (vid = upstream plan version, GET /plan/:id/version/:v) -----------------------------------------
        g.MapGet("/{type}/{id}/_history/{vid:int}", async (string type, string id, int vid, IPlanClient client, PlanMapper mapper, CancellationToken ct) =>
        {
            var found = await Resolve(type, id, vid, client, mapper, ct);
            return found.Result ?? Send(found.Resource!, etag: found.Resource!.Meta?.VersionId);
        });

        // ---- create: POST /fhir/CarePlan (body: CarePlan; zones as contained ServiceRequests) -------------
        // Creates a plan upstream (POST /plan) and returns 201 with the mapped CarePlan.
        g.MapPost("/CarePlan", async (HttpContext ctx, IPlanClient client, PlanMapper mapper, CancellationToken ct) =>
        {
            CarePlan? input;
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                input = System.Text.Json.JsonSerializer.Deserialize<CarePlan>(await reader.ReadToEndAsync(ct), FhirJson);
            }
            catch (Exception e) when (e is Hl7.Fhir.Serialization.DeserializationFailedException or System.Text.Json.JsonException or InvalidCastException or NotSupportedException)
            {
                return Outcome(400, OperationOutcome.IssueType.Invalid, "Body must be a FHIR CarePlan resource: " + e.Message);
            }
            if (input is null) return Outcome(400, OperationOutcome.IssueType.Invalid, "Body must be a FHIR CarePlan resource.");

            var (request, errors) = mapper.ToCreateRequest(input);
            if (request is null) return Outcome(422, OperationOutcome.IssueType.BusinessRule, string.Join(" ", errors));

            try
            {
                var created = await client.CreateAsync(request, ct);
                var plan = await client.GetAsync(created.Id, ct);
                if (plan is null) return Outcome(502, OperationOutcome.IssueType.Transient, "Plan was created but could not be read back.");
                ctx.Response.Headers.Location = $"{BaseUrl(ctx)}/CarePlan/{plan.Id}";
                return Send(mapper.Map(plan).CarePlan, 201, etag: plan.Version.ToString());
            }
            catch (UpstreamException e)
            {
                return e.Status switch
                {
                    400 => Outcome(400, OperationOutcome.IssueType.Invalid, e.Message),
                    409 => Outcome(409, OperationOutcome.IssueType.Conflict, e.Message),
                    422 => Outcome(422, OperationOutcome.IssueType.BusinessRule, e.Message),
                    501 => Outcome(501, OperationOutcome.IssueType.NotSupported, e.Message),
                    _ => Outcome(502, OperationOutcome.IssueType.Transient, e.Message),
                };
            }
        });

        // ---- operations: state changes on a plan, POST /fhir/CarePlan/{id}/$activate|$approve|$publish ----
        // Body (optional): Parameters with comment (string), revision (integer, optimistic lock; defaults to the
        // current revision), version (integer; defaults to the current plan version).
        // Success returns the updated CarePlan. Upstream 400/404/409/422 map to 400/404/409/422.
        foreach (var (name, command) in new[]
                 {
                     ("$activate", PlanCommands.Activate),
                     ("$approve", PlanCommands.Approve),
                     ("$publish", PlanCommands.Publish),
                 })
        {
            g.MapPost($"/CarePlan/{{id}}/{name}", async (string id, HttpContext ctx, IPlanClient client, PlanMapper mapper, CancellationToken ct) =>
            {
                Parameters? input = null;
                if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
                {
                    try
                    {
                        using var reader = new StreamReader(ctx.Request.Body);
                        var text = await reader.ReadToEndAsync(ct);
                        if (!string.IsNullOrWhiteSpace(text)) input = System.Text.Json.JsonSerializer.Deserialize<Parameters>(text, FhirJson);
                    }
                    catch (Exception e) when (e is Hl7.Fhir.Serialization.DeserializationFailedException or System.Text.Json.JsonException or InvalidCastException or NotSupportedException)
                    {
                        return Outcome(400, OperationOutcome.IssueType.Invalid, "Body must be a FHIR Parameters resource: " + e.Message);
                    }
                }
                var comment = (input?.GetSingleValue<FhirString>("comment"))?.Value;
                var revision = input?.GetSingleValue<Integer>("revision")?.Value;
                var version = input?.GetSingleValue<Integer>("version")?.Value;

                try
                {
                    var current = await client.GetAsync(id, ct);
                    if (current is null) return Outcome(404, OperationOutcome.IssueType.NotFound, $"CarePlan/{id} not found.");

                    await client.ExecuteAsync(id, command,
                        new CommandRequest(comment, revision ?? current.Revision, version ?? current.Version), ct);

                    var updated = await client.GetAsync(id, ct) ?? current;
                    return Send(mapper.Map(updated).CarePlan, etag: updated.Version.ToString());
                }
                catch (UpstreamException e)
                {
                    return e.Status switch
                    {
                        400 => Outcome(400, OperationOutcome.IssueType.Invalid, e.Message),
                        404 => Outcome(404, OperationOutcome.IssueType.NotFound, e.Message),
                        409 => Outcome(409, OperationOutcome.IssueType.Conflict, e.Message),
                        422 => Outcome(422, OperationOutcome.IssueType.BusinessRule, e.Message),
                        _ => Outcome(502, OperationOutcome.IssueType.Transient, e.Message),
                    };
                }
            });
        }

        // ---- $document: FHIR document Bundle (Composition + everything it references) -------------
        g.MapGet("/Composition/{id}/$document", async (string id, HttpContext ctx, IPlanClient client, PlanMapper mapper, CancellationToken ct) =>
        {
            var plan = await TryGet(id, client, ct);
            if (plan.Error is { } err) return err;
            var r = mapper.Map(plan.Plan!);

            var bundle = new Bundle
            {
                Type = Bundle.BundleType.Document,
                Identifier = new Identifier("urn:ietf:rfc:3986", $"urn:uuid:{Guid.NewGuid()}"),
                Timestamp = DateTimeOffset.UtcNow,
            };
            // Composition must be the first entry.
            foreach (var res in new Resource[] { r.Composition, r.Patient, r.CarePlan }
                         .Concat(r.ServiceRequests).Concat(r.Practitioner is null ? [] : [r.Practitioner]))
                bundle.Entry.Add(new Bundle.EntryComponent { FullUrl = $"{BaseUrl(ctx)}/{res.TypeName}/{res.Id}", Resource = res });
            return Send(bundle);
        });

        // ---- $everything: all resources derived from a plan ---------------------------------------
        g.MapGet("/CarePlan/{id}/$everything", async (string id, HttpContext ctx, IPlanClient client, PlanMapper mapper, CancellationToken ct) =>
        {
            var plan = await TryGet(id, client, ct);
            if (plan.Error is { } err) return err;
            var all = mapper.Map(plan.Plan!).All().ToList();
            var bundle = new Bundle { Type = Bundle.BundleType.Searchset, Total = all.Count };
            foreach (var res in all)
                bundle.Entry.Add(new Bundle.EntryComponent
                {
                    FullUrl = $"{BaseUrl(ctx)}/{res.TypeName}/{res.Id}", Resource = res,
                    Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Match },
                });
            return Send(bundle);
        });
    }

    // ------------------------------------------------------------------------------------------

    private sealed record Found(Resource? Resource, IResult? Result);

    /// <summary>Reads any derived resource by id, optionally at a specific plan version.</summary>
    private static async Task<Found> Resolve(string type, string id, int? version, IPlanClient client, PlanMapper mapper, CancellationToken ct)
    {
        try
        {
            if (type is "Patient" or "Practitioner")
                return new(null, Outcome(404, OperationOutcome.IssueType.NotSupported, $"{type} is only available through _include/$everything (no demographic data is stored)."));

            if (!Searchable.ContainsKey(type))
                return new(null, Outcome(404, OperationOutcome.IssueType.NotSupported, $"Resource type '{type}' is not supported."));

            var planId = PlanMapper.PlanIdFrom(id);
            if (planId is null) return new(null, Outcome(404, OperationOutcome.IssueType.NotFound, $"{type}/{id} not found."));

            if (type == "Provenance")
            {
                var ev = (await client.GetHistoryAsync(planId, ct)).FirstOrDefault(e => PlanMapper.ProvenanceId(planId, e.Revision) == id);
                return ev is null
                    ? new(null, Outcome(404, OperationOutcome.IssueType.NotFound, $"{type}/{id} not found."))
                    : new(mapper.MapEvent(planId, ev), null);
            }

            var plan = version is { } v ? await client.GetVersionAsync(planId, v, ct) : await client.GetAsync(planId, ct);
            if (plan is null) return new(null, Outcome(404, OperationOutcome.IssueType.NotFound, $"{type}/{id} not found."));

            var hit = Searchable[type](mapper.Map(plan)).FirstOrDefault(r => r.Id == id);
            return hit is null
                ? new(null, Outcome(404, OperationOutcome.IssueType.NotFound, $"{type}/{id} not found."))
                : new(hit, null);
        }
        catch (UpstreamException e) { return new(null, Outcome(e.Status == 404 ? 404 : 502, OperationOutcome.IssueType.Transient, e.Message)); }
    }

    private static async Task<(Plan? Plan, IResult? Error)> TryGet(string id, IPlanClient client, CancellationToken ct)
    {
        try
        {
            var plan = await client.GetAsync(id, ct);
            return plan is null ? (null, Outcome(404, OperationOutcome.IssueType.NotFound, $"Plan {id} not found.")) : (plan, null);
        }
        catch (UpstreamException e) { return (null, Outcome(502, OperationOutcome.IssueType.Transient, e.Message)); }
    }

    /// <summary>Accepts patient=NIN, patient=system|NIN and patient:identifier=system|NIN.</summary>
    private static string? PatientParam(IQueryCollection q)
    {
        var v = q["patient"].FirstOrDefault() ?? q["patient:identifier"].FirstOrDefault() ?? q["subject:identifier"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(v)) return null;
        var i = v.LastIndexOf('|');
        return i >= 0 ? v[(i + 1)..] : v;
    }

    // Builds a paging link. The patient identifier is deliberately not echoed back.
    private static string PageUrl(HttpContext ctx, int offset)
    {
        var pairs = ctx.Request.Query
            .Where(kv => !kv.Key.StartsWith("patient") && !kv.Key.StartsWith("subject") && kv.Key != "_offset")
            .SelectMany(kv => kv.Value.Select(v => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(v ?? "")}"))
            .Prepend("patient=%5Bredacted%5D").Append($"_offset={offset}");
        return $"{BaseUrl(ctx)}{ctx.Request.Path.Value![(ctx.Request.PathBase.Value ?? "").Length..].Replace("/fhir", "")}?{string.Join("&", pairs)}";
    }

    private static string BaseUrl(HttpContext ctx) =>
        $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}/fhir";

    private static CapabilityStatement Capability(HttpContext ctx)
    {
        CapabilityStatement.ResourceComponent Res(string type, params (string name, SearchParamType t)[] sp) => new()
        {
            Type = type,
            Interaction =
            [
                new() { Code = CapabilityStatement.TypeRestfulInteraction.Read },
                new() { Code = CapabilityStatement.TypeRestfulInteraction.SearchType },
            ],
            SearchParam = sp.Select(s => new CapabilityStatement.SearchParamComponent { Name = s.name, Type = s.t }).ToList(),
        };

        CapabilityStatement.ResourceComponent WithCreate(CapabilityStatement.ResourceComponent r)
        {
            r.Interaction.Add(new() { Code = CapabilityStatement.TypeRestfulInteraction.Create });
            return r;
        }

        var patient = ("patient", SearchParamType.Token);
        var status = ("status", SearchParamType.Token);
        var lastUpdated = ("_lastUpdated", SearchParamType.Date);

        return new CapabilityStatement
        {
            Status = PublicationStatus.Active,
            Date = "2026-11-09",
            Kind = CapabilityStatementKind.Instance,
            FhirVersion = FHIRVersion.N5_0_0,
            Format = ["application/fhir+json"],
            Implementation = new CapabilityStatement.ImplementationComponent { Description = "Planer FHIR R5 facade", Url = BaseUrl(ctx) },
            Rest =
            [
                new CapabilityStatement.RestComponent
                {
                    Mode = CapabilityStatement.RestfulCapabilityMode.Server,
                    Operation =
                    [
                        new() { Name = "activate", Definition = "http://localhost/OperationDefinition/careplan-activate" },
                        new() { Name = "approve", Definition = "http://localhost/OperationDefinition/careplan-approve" },
                        new() { Name = "publish", Definition = "http://localhost/OperationDefinition/careplan-publish" },
                    ],
                    Resource =
                    [
                        WithCreate(Res("CarePlan", patient, status, lastUpdated, ("published", SearchParamType.Token))),
                        Res("Composition", patient, status, lastUpdated),
                        Res("ServiceRequest", patient, status, lastUpdated),
                        Res("Task", patient, status, lastUpdated),
                        Res("PlanDefinition"),
                        Res("Provenance", patient, ("target", SearchParamType.Reference), lastUpdated),
                    ],
                },
            ],
        };
    }
}
