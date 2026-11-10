# Planer FHIR R5 facade

> **Proof of concept.** This project was made for the **HL7 Norge FHIR hackathon 2026**. It is a demonstration of
> how the Planer API could be offered as FHIR. It is not a production service, it is not conformant to a Norwegian
> FHIR profile, and it has not been through the HL7 validator.

## What it is

The Planer API ("API for managing plans with clinical content", the Bruno collection **Planer API - dev nhn**) is
the service behind personal action plans (*egenbehandlingsplaner*) with a personal goal and green, yellow and red zones,
an approval flow and publication to the patient's view. It has its own JSON API at
`https://planer.dev.nhn.no/api/personnel`.

This project is a **facade**: an ASP.NET Core (.NET 10) service that sits in front of that API and translates it to
**FHIR R5** (Firely SDK `Hl7.Fhir.R5`, `application/fhir+json`). It has no database and no state of its own. Every
FHIR request is turned into calls to the upstream, and the answer is mapped back to FHIR resources.

```text
FHIR client  ->  Planer FHIR facade  ->  Planer API (planer.dev.nhn.no/api/personnel)
              FHIR R5 (/fhir)          its own JSON API (/plan, /plan/:id, ...)
```

The services it translates, with the upstream calls they use:

| Upstream service (Bruno `plan/`)                                  | Through the facade                                           |
|-------------------------------------------------------------------|--------------------------------------------------------------|
| `GET /plan`, `/plan/:id`, `/plan/:id/version/:v`                  | Search and read of `CarePlan`, `Composition`, `ServiceRequest`, `Task`, `Practitioner`, `Patient`; `vread` |
| `GET /plan/:id/history`                                           | `Provenance`                                                 |
| `POST /plan`                                                      | `POST /fhir/CarePlan` (create)                               |
| `POST /plan/:id/$activate`, `$approval-given`, `$mark-published`  | FHIR operations `$activate`, `$approve`, `$publish` on `CarePlan` |
| (none: the upstream has no templates)                             | A static `PlanDefinition` describing the plan structure      |

Not exposed yet: update of plan content, reject, request approval, unpublish, deactivate, and the `active`/`latest`/`current`
reads as separate endpoints.

```bash
dotnet run          # http://localhost:5080/fhir/metadata, calls the real dev upstream
```

Requests to try are in `PlanerFhir.http`, and Bruno requests are in the `Fhir/` folder of the collection. There is no
mock mode: every call goes to the real upstream, including create and the operations, which change real data.

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

- `Upstream:BaseUrl` (`https://planer.dev.nhn.no/api/personnel`): every request goes to this real API and the caller's `Authorization` header is forwarded.
- `Fhir:BaseUrl` — used for code system URLs. `Fhir:NinSystem` / `HprSystem` — identifier systems.

## Caveats

- The `urn:example:*` systems and `<BaseUrl>/CodeSystem/*` are placeholders — replace with your real ones.
- The facade does no authentication or authorisation of its own. It forwards the caller's `Authorization` header to the upstream.
- The FHIR modelling is a proposal made for the hackathon (for example plan → `CarePlan` + `Composition`, approval → `Task`); it is not agreed with the owners of the Planer API.
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

Invalid content gives `422`, upstream 400/409/422 map to the same status, other upstream errors to `502`. Create really creates a plan in the upstream (`planer.dev.nhn.no` by default).

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

Reject, request approval, unpublish and deactivate are not exposed yet.
