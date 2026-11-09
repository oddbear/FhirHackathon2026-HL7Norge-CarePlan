# Planer FHIR R5 facade

ASP.NET Core (.NET 10) API that exposes the plans from the Bruno collection **Planer API – dev nhn**
(`GET /plan`, `/plan/:id`, `/plan/:id/history`, `/plan/:id/version/:v`) as **FHIR R5** (Firely SDK `Hl7.Fhir.R5`).
Read/search only; the collection's write operations (`$activate`, `$approval-*`, ...) are not exposed.

```bash
dotnet run          # http://localhost:5080/fhir/metadata, calls the real dev upstream by default
```

Requests to try are in `PlanerFhir.http`.

## Mapping

| Upstream                          | FHIR R5                                                                          |
|-----------------------------------|----------------------------------------------------------------------------------|
| plan                              | `CarePlan` (id = plan id, `meta.versionId` = version)                            |
| plan (same id)                    | `Composition` type LOINC 18776-5, one section per zone under a "Plan" section    |
| `clinical_content.zones[]`        | `ServiceRequest` `{planId}-zone-{n}`, `basedOn` CarePlan, priority green→routine, yellow→urgent, red→asap, `reason` = condition, `code` = action; also `CarePlan.activity.plannedActivityReference` |
| `personal_goals`                  | `CarePlan.description` + `goal.display`, Composition section                     |
| `approvals[]`                     | `Task` `{planId}-approval-{n}` (requested→requested, approved→completed, rejected→rejected) |
| `publication_status[]`            | `Task` `{planId}-publication-{n}` (published→completed, unpublished→cancelled), `owner` = provider |
| `updated_by` / `updated_at`       | `Practitioner` (HPR + NIN identifiers) and `Provenance`                          |
| `/history` events                 | `Provenance` `{planId}-rev-{revision}` (activity CREATE/UPDATE + the upstream `event_type`, comment as extension) |
| `subject.nin`                     | `Patient` with NIN identifier (id is a hash — the NIN never appears in URLs)     |

Status: `CarePlan`/`ServiceRequest` are `active` when upstream `is_active` is true (falls back to `active_version == version`),
otherwise `draft`. Approval status `given` (also `approved`) maps to a completed Task and a `final` Composition.

## Notes from the real dev data (checked 2026-11-09 for one patient)

- `GET /plan?patient=` returns **everything in one response, unpaged** (17,679 plans, ~13 MB, ~2 s). Paging is done here: `_count` (default 50, max 200) and `_offset`, with `next` links.
- `active_version`, `approvals` and `publication_status` are usually `null`; `is_active` is a boolean.
- `/plan/:id/history` returns **audit events** (`plan_created`, `approval_given`, ...), not plan versions, so they are exposed as `Provenance`
  (`Provenance?target=CarePlan/{id}`), not as a FHIR `_history` bundle.
- `updated_by.nin` is often empty; the practitioner is then identified by HPR number only.

## Search

`GET /fhir/{CarePlan|Composition|ServiceRequest|Task|Provenance}`

| FHIR parameter                  | Upstream            |
|---------------------------------|---------------------|
| `patient` or `patient:identifier` (required) | `patient` |
| `status=active` / other         | `is_active=true/false` |
| `_lastUpdated=ge<dateTime>`     | `updated_since`     |
| `published=true/false` (custom) | `published`         |
| `_count`, `_offset`             | paging done locally (upstream is unpaged) |
| `_include`: `CarePlan:subject`, `CarePlan:author`, `ServiceRequest:based-on` | |
| `_revinclude`: `ServiceRequest:based-on`, `Task:focus`, `Composition:entry`, `Provenance:target` | |

`Provenance?target=CarePlan/{id}` lists a plan's audit events. Other: `GET /fhir/{type}/{id}`, `CarePlan/{id}/_history/{n}` (n = plan version, upstream `/version/{n}`), `Composition/{id}/$document`, `CarePlan/{id}/$everything`, `/metadata`.

## Configuration (`appsettings.json`)

- `Upstream:Mode` = `Http` (default) or `Mock` (offline sample data). With `Http`, requests go to `Upstream:BaseUrl`
  (`https://planer.dev.nhn.no/api/personnel`) and the caller's `Authorization` header is forwarded.
- `Fhir:BaseUrl` — used for code system URLs. `Fhir:NinSystem` / `HprSystem` — identifier systems.

## Caveats

- The `urn:example:*` systems and `<BaseUrl>/CodeSystem/*` are placeholders — replace with your real ones.
- The dev API currently needs no auth; if that changes, the caller's `Authorization` header is forwarded.
- Each search downloads the patient's full plan list upstream, so very large patients are slow; consider caching.
- The NIN is a query parameter (as upstream requires); the `self` link redacts it, but access logs will contain it.
- Output has been parsed/serialised by the SDK but not run through the HL7 validator.

## PlanDefinition

`GET /fhir/PlanDefinition` and `GET /fhir/PlanDefinition/personal-plan` return a static definition of the plan
structure: personal goals plus a green, yellow and red zone, each with a condition and a `ServiceRequest` action.
The upstream has no template resource, so this is not read from it. Every `CarePlan` references it in
`instantiatesCanonical`. No patient parameter is needed, and there is no upstream call.

## Create a plan

`POST /fhir/CarePlan` with a CarePlan body creates a plan upstream (`POST /plan`) and returns `201` with the CarePlan and a `Location` header.

| FHIR | Upstream |
|---|---|
| `title` (required) | `clinical_content.title` |
| `description` | `clinical_content.personal_goals` |
| `subject.identifier.value` (required, NIN) | `subject.nin` |
| `note[0].text` | `comment` |
| contained `ServiceRequest`: `category.coding.code` green/yellow/red, `code.text`, `reason.concept.text` | `clinical_content.zones[]` level, action, condition |

Invalid content gives `422`, upstream 400/409/422 map to the same status, other upstream errors to `502`. In mock mode the plan is kept in memory only.

## Operations (state changes)

The three upstream commands are exposed as FHIR operations on `CarePlan`, not as writes to `Task`: they are
commands with their own rules, not edits of a resource, and the upstream decides the resulting state. The `Task`
resources stay read-only and show the result.

| Operation | Upstream call |
|---|---|
| `POST /fhir/CarePlan/{id}/$activate` | `POST /plan/:id/$activate` |
| `POST /fhir/CarePlan/{id}/$approve` | `POST /plan/:id/$approval-given` |
| `POST /fhir/CarePlan/{id}/$publish` | `POST /plan/:id/$mark-published` |

The upstream requires `revision` and `version` on every command, so both are always sent. The optional body is a
`Parameters` resource with `comment` (string), `revision` (integer) and `version` (integer). The upstream uses `revision` as an optimistic lock. If you leave it out, the current revision is read
first and used, so the lock does not protect against concurrent edits unless you send it. `version` defaults to
the current plan version, read the same way. A successful call returns the updated `CarePlan`.

Upstream errors keep their meaning: 400 → 400 `invalid`, 404 → 404, 409 → 409 `conflict` (stale revision),
422 → 422 `business-rule`, anything else → 502. The caller's `Authorization` header is forwarded.

In Mock mode the operations check the revision and succeed, but they do not change the sample data.
Reject, request approval, unpublish and deactivate are not exposed yet.
