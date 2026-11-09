using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hl7.Fhir.Model;
using PlanerFhir.Upstream;
using Task = Hl7.Fhir.Model.Task;

namespace PlanerFhir.Mapping;

public sealed class FhirOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5080/fhir";
    /// <summary>Identifier system used for Norwegian national identity numbers (fnr/dnr).</summary>
    public string NinSystem { get; set; } = "urn:oid:2.16.578.1.12.4.1.4.1";
    public string HprSystem { get; set; } = "urn:oid:2.16.578.1.12.4.1.4.4";
    public string ProviderSystem { get; set; } = "urn:example:planer:provider-id";
    public string PlanSystem { get; set; } = "urn:example:planer:plan-id";
}

/// <summary>The set of FHIR resources derived from one version of an upstream plan.</summary>
public sealed record PlanResources(
    CarePlan CarePlan,
    Composition Composition,
    IReadOnlyList<ServiceRequest> ServiceRequests,
    IReadOnlyList<Task> Tasks,
    Provenance? Provenance,
    Patient Patient,
    Practitioner? Practitioner)
{
    public IEnumerable<Resource> All()
    {
        yield return CarePlan;
        yield return Composition;
        foreach (var s in ServiceRequests) yield return s;
        foreach (var t in Tasks) yield return t;
        if (Provenance is not null) yield return Provenance;
        yield return Patient;
        if (Practitioner is not null) yield return Practitioner;
    }
}

/// <summary>
/// Upstream plan -> FHIR R5.
///   Plan                       -> CarePlan  (+ Composition as the human-readable plan document)
///   clinical_content.zones[]   -> ServiceRequest (one per zone, priority by colour) and a Composition section each
///   approvals[]                -> Task (code "approve")
///   publication_status[]       -> Task (code "publish-plan")
///   updated_by / updated_at    -> Provenance + Practitioner
///   subject.nin                -> Patient (identifier only, no demographics are known)
/// </summary>
public sealed class PlanMapper(Microsoft.Extensions.Options.IOptions<FhirOptions> options)
{
    private readonly FhirOptions _o = options.Value;

    private string ZoneSystem => $"{_o.BaseUrl}/CodeSystem/plan-zone";
    private string PlanSystemUrl => _o.PlanSystem;

    // ---- ids ---------------------------------------------------------------------------------

    /// <summary>The NIN must not appear in resource ids / URLs, so the Patient id is a salted-free hash prefix.</summary>
    public static string PatientId(string nin) =>
        "p-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nin)))[..16].ToLowerInvariant();

    public static string PractitionerId(Author a) =>
        !string.IsNullOrEmpty(a.HprNumber) ? $"hpr-{a.HprNumber}" : "pr-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(a.Nin ?? a.DisplayName ?? "unknown")))[..16].ToLowerInvariant();

    public static string ServiceRequestId(string planId, int index) => $"{planId}-zone-{index + 1}";
    public static string ProvenanceId(string planId, int revision) => $"{planId}-rev-{revision}";
    public static string TaskId(string planId, string kind, int index) => $"{planId}-{kind}-{index + 1}";

    /// <summary>Plan ids are GUIDs (36 chars); derived resource ids are prefixed with the plan id.</summary>
    public static string? PlanIdFrom(string resourceId) =>
        resourceId.Length >= 36 && Guid.TryParse(resourceId.AsSpan(0, 36), out _) ? resourceId[..36] : null;

    private ResourceReference Ref(Resource r) => new($"{r.TypeName}/{r.Id}");

    // ---- main --------------------------------------------------------------------------------

    public PlanResources Map(Plan plan)
    {
        var patient = MapPatient(plan.Subject.Nin);
        var practitioner = plan.UpdatedBy is null ? null : MapPractitioner(plan.UpdatedBy);

        var carePlan = MapCarePlan(plan, patient, practitioner);
        var requests = MapServiceRequests(plan, carePlan, patient, practitioner);
        var tasks = MapTasks(plan, carePlan, patient);
        var composition = MapComposition(plan, carePlan, requests, patient, practitioner);
        var provenance = practitioner is null ? null : MapProvenance(plan, carePlan, practitioner);

        // CarePlan.activity points at the ServiceRequests so the plan is navigable from CarePlan alone.
        foreach (var sr in requests)
            carePlan.Activity.Add(new CarePlan.ActivityComponent { PlannedActivityReference = Ref(sr) });

        return new PlanResources(carePlan, composition, requests, tasks, provenance, patient, practitioner);
    }

    public string PlanStatusText(Plan p) => IsActive(p) ? "active" : "draft";
    private static bool IsActive(Plan p) => p.IsActive ?? (p.ActiveVersion is > 0 && p.ActiveVersion == p.Version);

    private static Approval? LatestApproval(Plan p) =>
        p.Approvals?.Where(a => a.Version == p.Version).OrderByDescending(a => a.UpdatedAt).FirstOrDefault();

    // ---- Patient / Practitioner -------------------------------------------------------------

    public Patient MapPatient(string nin) => new()
    {
        Id = PatientId(nin),
        Identifier = [new Identifier(_o.NinSystem, nin) { Use = Identifier.IdentifierUse.Official }],
    };

    public Practitioner MapPractitioner(Author a)
    {
        var pr = new Practitioner { Id = PractitionerId(a) };
        if (!string.IsNullOrEmpty(a.HprNumber)) pr.Identifier.Add(new Identifier(_o.HprSystem, a.HprNumber));
        if (!string.IsNullOrEmpty(a.Nin)) pr.Identifier.Add(new Identifier(_o.NinSystem, a.Nin));
        if (!string.IsNullOrEmpty(a.DisplayName)) pr.Name.Add(new HumanName { Text = a.DisplayName });
        return pr;
    }

    // ---- CarePlan ----------------------------------------------------------------------------

    private CarePlan MapCarePlan(Plan p, Patient patient, Practitioner? author)
    {
        var cp = new CarePlan
        {
            Id = p.Id,
            Meta = Meta(p),
            Identifier = [new Identifier(PlanSystemUrl, p.Id)],
            Status = IsActive(p) ? RequestStatus.Active : RequestStatus.Draft,
            Intent = CarePlan.CarePlanIntent.Plan,
            Category = [Cc("http://snomed.info/sct", "734163000", "Care plan")],
            Title = p.ClinicalContent.Title,
            Description = p.ClinicalContent.PersonalGoals,
            Subject = Ref(patient),
            Created = p.UpdatedAt.ToString("o"),
        };
        cp.Subject.Identifier = new Identifier(_o.NinSystem, p.Subject.Nin);
        if (author is not null) cp.Contributor.Add(Ref(author));
        if (!string.IsNullOrWhiteSpace(p.Comment)) cp.Note.Add(new Annotation { Text = new Markdown(p.Comment) });
        if (!string.IsNullOrWhiteSpace(p.ClinicalContent.PersonalGoals))
            cp.Goal.Add(new ResourceReference { Display = p.ClinicalContent.PersonalGoals });
        return cp;
    }

    // ---- ServiceRequest (one per zone) -------------------------------------------------------

    private IReadOnlyList<ServiceRequest> MapServiceRequests(Plan p, CarePlan cp, Patient patient, Practitioner? author)
    {
        var list = new List<ServiceRequest>();
        var zones = p.ClinicalContent.Zones ?? [];
        for (var i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            var level = z.Level.ToLowerInvariant();
            var sr = new ServiceRequest
            {
                Id = ServiceRequestId(p.Id, i),
                Meta = Meta(p),
                Status = IsActive(p) ? RequestStatus.Active : RequestStatus.Draft,
                Intent = RequestIntent.Plan,
                Priority = level switch { "red" => RequestPriority.Asap, "yellow" => RequestPriority.Urgent, _ => RequestPriority.Routine },
                Category = [Cc(ZoneSystem, level, ZoneDisplay(level))],
                Code = new CodeableReference(new CodeableConcept { Text = z.Action }),
                Subject = Ref(patient),
                BasedOn = [Ref(cp)],
                AuthoredOn = p.UpdatedAt.ToString("o"),
                Reason = [new CodeableReference(new CodeableConcept { Text = z.Condition })],
            };
            if (author is not null) sr.Requester = Ref(author);
            list.Add(sr);
        }
        return list;
    }

    // ---- Task (approvals + publications) -----------------------------------------------------

    private IReadOnlyList<Task> MapTasks(Plan p, CarePlan cp, Patient patient)
    {
        var tasks = new List<Task>();

        var approvals = p.Approvals ?? [];
        for (var i = 0; i < approvals.Count; i++)
        {
            var a = approvals[i];
            tasks.Add(new Task
            {
                Id = TaskId(p.Id, "approval", i),
                Status = a.Status.ToLowerInvariant() switch
                {
                    "requested" => Task.TaskStatus.Requested,
                    "approved" or "given" => Task.TaskStatus.Completed,
                    "rejected" => Task.TaskStatus.Rejected,
                    _ => Task.TaskStatus.Draft,
                },
                BusinessStatus = new CodeableConcept { Text = a.Status },
                Intent = Task.TaskIntent.Order,
                Code = Cc("http://hl7.org/fhir/CodeSystem/task-code", "approve", "Activate/approve the focus resource"),
                Description = $"Approval of plan version {a.Version}",
                Focus = Ref(cp),
                For = Ref(patient),
                AuthoredOn = a.UpdatedAt.ToString("o"),
                LastModified = a.UpdatedAt.ToString("o"),
                Meta = new Meta { VersionId = a.Version.ToString(), LastUpdated = a.UpdatedAt },
            });
        }

        var pubs = p.PublicationStatus ?? [];
        for (var i = 0; i < pubs.Count; i++)
        {
            var pub = pubs[i];
            tasks.Add(new Task
            {
                Id = TaskId(p.Id, "publication", i),
                Status = pub.Status.ToLowerInvariant() switch
                {
                    "published" => Task.TaskStatus.Completed,
                    "unpublished" => Task.TaskStatus.Cancelled,
                    _ => Task.TaskStatus.Draft,
                },
                BusinessStatus = new CodeableConcept { Text = pub.Status },
                Intent = Task.TaskIntent.Order,
                Code = new CodeableConcept { Coding = [new Coding($"{_o.BaseUrl}/CodeSystem/plan-task", "publish-plan", "Publish plan to provider")] },
                Description = $"Publication of plan version {pub.Version} to {pub.ProviderId}",
                Focus = Ref(cp),
                For = Ref(patient),
                Owner = new ResourceReference { Type = "Organization", Identifier = new Identifier(_o.ProviderSystem, pub.ProviderId) },
                AuthoredOn = pub.UpdatedAt.ToString("o"),
                LastModified = pub.UpdatedAt.ToString("o"),
                Meta = new Meta { VersionId = pub.Version.ToString(), LastUpdated = pub.UpdatedAt },
            });
        }
        return tasks;
    }

    // ---- Composition -------------------------------------------------------------------------

    private Composition MapComposition(Plan p, CarePlan cp, IReadOnlyList<ServiceRequest> requests, Patient patient, Practitioner? author)
    {
        var latest = LatestApproval(p);
        var comp = new Composition
        {
            Id = p.Id,
            Meta = Meta(p),
            Identifier = [new Identifier(PlanSystemUrl, p.Id)],
            Status = IsActive(p) && latest?.Status is "approved" or "given" ? CompositionStatus.Final : CompositionStatus.Preliminary,
            Type = Cc("http://loinc.org", "18776-5", "Plan of care note"),
            Subject = [Ref(patient)],
            Date = p.UpdatedAt.ToString("o"),
            Version = p.Version.ToString(),
            Title = p.ClinicalContent.Title,
            Author = author is null ? [new ResourceReference { Display = "Unknown" }] : [Ref(author)],
        };

        if (!string.IsNullOrWhiteSpace(p.ClinicalContent.PersonalGoals))
        {
            comp.Section.Add(new Composition.SectionComponent
            {
                Title = "Personlige mål",
                Code = Cc("http://loinc.org", "61146-7", "Goals"),
                Text = Narrative($"<p>{Enc(p.ClinicalContent.PersonalGoals)}</p>"),
            });
        }

        var zones = p.ClinicalContent.Zones ?? [];
        var plan = new Composition.SectionComponent
        {
            Title = "Plan",
            Code = Cc("http://loinc.org", "18776-5", "Plan of care note"),
            Entry = [Ref(cp)],
        };
        for (var i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            var level = z.Level.ToLowerInvariant();
            plan.Section.Add(new Composition.SectionComponent
            {
                Title = ZoneDisplay(level),
                Code = Cc(ZoneSystem, level, ZoneDisplay(level)),
                Text = Narrative($"<p><b>Tilstand:</b> {Enc(z.Condition)}</p><p><b>Tiltak:</b> {Enc(z.Action)}</p>"),
                Entry = [Ref(requests[i])],
            });
        }
        comp.Section.Add(plan);
        return comp;
    }

    // ---- Provenance --------------------------------------------------------------------------

    private Provenance MapProvenance(Plan p, CarePlan cp, Practitioner author) =>
        MapEvent(p.Id, new HistoryEvent
        {
            Revision = p.Revision, Version = p.Version, UpdatedAt = p.UpdatedAt, UpdatedBy = p.UpdatedBy,
            Comment = p.Comment, EventType = p.Revision <= 1 ? "plan_created" : "plan_updated",
        }, author);

    /// <summary>An upstream audit event (GET /plan/:id/history) as a Provenance for the CarePlan.</summary>
    public Provenance MapEvent(string planId, HistoryEvent e, Practitioner? who = null)
    {
        who ??= e.UpdatedBy is null ? null : MapPractitioner(e.UpdatedBy);
        var pv = new Provenance
        {
            Id = ProvenanceId(planId, e.Revision),
            Meta = new Meta { VersionId = e.Revision.ToString(), LastUpdated = e.UpdatedAt },
            Target = [new ResourceReference($"CarePlan/{planId}/_history/{e.Version}")],
            Occurred = new FhirDateTime(e.UpdatedAt),
            Recorded = e.UpdatedAt,
            Activity = new CodeableConcept
            {
                Coding =
                [
                    new Coding("http://terminology.hl7.org/CodeSystem/v3-DataOperation", e.EventType == "plan_created" ? "CREATE" : "UPDATE"),
                    new Coding($"{_o.BaseUrl}/CodeSystem/plan-event", e.EventType),
                ],
                Text = e.EventType,
            },
            Agent = [new Provenance.AgentComponent { Who = who is null ? new ResourceReference { Display = "Unknown" } : Ref(who) }],
        };
        if (!string.IsNullOrWhiteSpace(e.Comment))
            pv.Extension.Add(new Extension($"{_o.BaseUrl}/StructureDefinition/event-comment", new FhirString(e.Comment)));
        return pv;
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static CodeableConcept Cc(string system, string code, string display) =>
        new() { Coding = [new Coding(system, code, display)], Text = display };

    private static Meta Meta(Plan p) => new() { VersionId = p.Version.ToString(), LastUpdated = p.UpdatedAt };

    private static string ZoneDisplay(string level) => level switch
    {
        "green" => "Grønn sone", "yellow" => "Gul sone", "red" => "Rød sone", _ => level,
    };

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    private static Narrative Narrative(string xhtmlBody) => new()
    {
        Status = Hl7.Fhir.Model.Narrative.NarrativeStatus.Generated,
        Div = $"<div xmlns=\"http://www.w3.org/1999/xhtml\">{xhtmlBody}</div>",
    };
}
