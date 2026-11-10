# Planer API: Bruno collection and FHIR facade

> **Proof of concept** made for the **HL7 Norge FHIR hackathon 2026**. Not a production service.

This repository has the Bruno collection **Planer API - dev nhn** for NHN's Planer API (version 3.0, "API for managing
plans with clinical content", the personal action plans of Pasientens planer), and a **FHIR R5 facade** in front of it.

## Folders

| Folder | Content |
|---|---|
| [`PlanerFhir/`](PlanerFhir/README.md) | The C# (ASP.NET Core, .NET 10) facade that translates the Planer API to FHIR R5. **Start with [`PlanerFhir/README.md`](PlanerFhir/README.md)** for what it exposes, the mapping, configuration and caveats. |
| [`Fhir/`](Fhir) | Bruno requests for the facade (`http://localhost:5080/fhir`) |
| [`plan/`](plan) | Bruno requests for the Planer API itself (`https://planer.dev.nhn.no/api/personnel`) |
| [`environments/`](environments) | Bruno environment with `baseUrl` |

## Quick start

```bash
cd PlanerFhir
dotnet run        # http://localhost:5080/fhir/metadata
```

Then open the collection in Bruno and run the requests in `Fhir/`. The facade calls the real dev API. There is no mock,
so create and the operations (`$activate`, `$approve`, `$publish`) change real data on the dev environment.

## Links

- [Planer API Swagger](https://planer.dev.nhn.no/api/personnel/swagger/index.html) (dev)
- [Test data](https://utviklerportal.nhn.no/informasjonstjenester/pasientens-planer/planer/planer-monorepo/docs/testdatamd) on NHN Utviklerportal
  (dev and test accept only synthetic national identity numbers; get them from [syntpop.nhn.no](https://syntpop.nhn.no)
  or calculate them. The collection's `nin` variable is one of them.)
