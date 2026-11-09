namespace PlanerFhir.Upstream;

/// <summary>In-memory sample data so the API runs without access to planer.dev.nhn.no.</summary>
public sealed class MockPlanClient : IPlanClient
{
    public const string ActivePlanId = "602c59a6-4be9-46b8-a123-502c5e226b6b";
    public const string DraftPlanId = "7d1f3c0e-2a55-4b8e-9d34-1c6f0a9b7e21";
    public const string Nin = "18679135941";

    private static readonly Author Nurse = new() { DisplayName = "Kari Nordmann", HprNumber = "9144900", Nin = "01010112345" };
    private static readonly Author Doctor = new() { DisplayName = "Ola Lege", HprNumber = "4567890", Nin = "02020254321" };

    private static readonly List<Zone> Zones =
    [
        new() { Level = "green", Condition = "Stabil pust, kan gå tur daglig", Action = "Fortsett vanlig medisinering og daglig gange" },
        new() { Level = "yellow", Condition = "Økt tung pust eller økt slimproduksjon", Action = "Ta ekstra dose inhalasjon og kontakt fastlege innen 24 timer" },
        new() { Level = "red", Condition = "Kan ikke snakke i hele setninger, blålig hud", Action = "Ring 113 umiddelbart" },
    ];

    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    // Version 1 (draft, approval requested) -> version 2 (approved, active, published).
    private static readonly Plan ActiveV1 = new()
    {
        Id = ActivePlanId, Version = 1, Revision = 1, ActiveVersion = null, IsActive = false,
        Subject = new() { Nin = Nin }, UpdatedAt = T0, UpdatedBy = Nurse,
        Comment = "Første utkast",
        ClinicalContent = new() { Title = "Egenbehandlingsplan KOLS", PersonalGoals = "Gå tur med hunden hver dag", Zones = Zones[..2] },
        Approvals = [new() { Status = "requested", Version = 1, UpdatedAt = T0 }],
        PublicationStatus = [],
    };

    private static readonly Plan ActiveV2 = ActiveV1 with
    {
        Version = 2, Revision = 3, ActiveVersion = 2, IsActive = true, UpdatedAt = T0.AddDays(7), UpdatedBy = Doctor,
        Comment = "Lagt til rød sone, godkjent av lege",
        ClinicalContent = ActiveV1.ClinicalContent with { Zones = Zones },
        Approvals =
        [
            new() { Status = "requested", Version = 2, UpdatedAt = T0.AddDays(6) },
            new() { Status = "given", Version = 2, UpdatedAt = T0.AddDays(7) },
        ],
        PublicationStatus = [new() { ProviderId = "kjernejournal", Status = "published", Version = 2, UpdatedAt = T0.AddDays(7).AddMinutes(5) }],
    };

    private static readonly Plan Draft = new()
    {
        Id = DraftPlanId, Version = 1, Revision = 2, ActiveVersion = null, IsActive = false,
        Subject = new() { Nin = Nin }, UpdatedAt = T0.AddDays(20), UpdatedBy = Nurse,
        Comment = "Venter på godkjenning",
        ClinicalContent = new() { Title = "Kriseplan ved hjertesvikt", PersonalGoals = "Unngå innleggelse", Zones = [Zones[0], Zones[2]] },
        Approvals = [new() { Status = "requested", Version = 1, UpdatedAt = T0.AddDays(20) }],
        PublicationStatus = [],
    };

    private static readonly Plan[] All = [ActiveV2, Draft];

    public Task<IReadOnlyList<Plan>> SearchAsync(PlanQuery q, CancellationToken ct)
    {
        IEnumerable<Plan> r = All.Where(p => p.Subject.Nin == q.Patient);
        if (q.IsActive is { } a) r = r.Where(p => (p.IsActive ?? false) == a);
        if (q.UpdatedSince is { } u) r = r.Where(p => p.UpdatedAt >= u);
        if (q.Published is { } pub) r = r.Where(p => (p.PublicationStatus?.Any(x => x.Status == "published") ?? false) == pub);
        return Task.FromResult<IReadOnlyList<Plan>>(r.ToList());
    }

    public Task<Plan?> GetAsync(string id, CancellationToken ct) =>
        Task.FromResult(All.FirstOrDefault(p => p.Id == id));

    public Task<IReadOnlyList<HistoryEvent>> GetHistoryAsync(string id, CancellationToken ct)
    {
        IReadOnlyList<HistoryEvent> ev = id switch
        {
            ActivePlanId =>
            [
                new() { Revision = 1, Version = 1, EventType = "plan_created", UpdatedAt = T0, UpdatedBy = Nurse, Comment = "Første utkast" },
                new() { Revision = 2, Version = 2, EventType = "plan_content_updated", UpdatedAt = T0.AddDays(6), UpdatedBy = Doctor, Comment = "Lagt til rød sone" },
                new() { Revision = 3, Version = 2, EventType = "approval_given", UpdatedAt = T0.AddDays(7), UpdatedBy = Doctor, Comment = "Godkjent", Approvals = 1 },
            ],
            DraftPlanId =>
            [
                new() { Revision = 1, Version = 1, EventType = "plan_created", UpdatedAt = T0.AddDays(19), UpdatedBy = Nurse },
                new() { Revision = 2, Version = 1, EventType = "approval_requested", UpdatedAt = T0.AddDays(20), UpdatedBy = Nurse, Approvals = 1 },
            ],
            _ => [],
        };
        return Task.FromResult(ev);
    }

    // Offline stub: checks the revision like the real API would, but does not change the sample data.
    public Task<CommandResult> ExecuteAsync(string id, string command, CommandRequest r, CancellationToken ct)
    {
        var plan = All.FirstOrDefault(p => p.Id == id) ?? throw new UpstreamException("Plan not found", 404, "not_found");
        if (r.Revision != plan.Revision)
            throw new UpstreamException($"Revision mismatch: expected {plan.Revision}, got {r.Revision}", 409, "revision_conflict");
        return Task.FromResult(new CommandResult(id, plan.Revision + 1));
    }

    public Task<Plan?> GetVersionAsync(string id, int version, CancellationToken ct) =>
        Task.FromResult(version == 1 && id == ActivePlanId ? ActiveV1 : All.FirstOrDefault(p => p.Id == id && p.Version == version));
}
