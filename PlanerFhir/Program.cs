using PlanerFhir.Endpoints;
using PlanerFhir.Mapping;
using PlanerFhir.Upstream;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<FhirOptions>(builder.Configuration.GetSection("Fhir"));
builder.Services.AddSingleton<PlanMapper>();
builder.Services.AddHttpContextAccessor();

// The real Planer API at Upstream:BaseUrl; the caller's Authorization header is forwarded.
var baseUrl = builder.Configuration["Upstream:BaseUrl"]
    ?? throw new InvalidOperationException("Upstream:BaseUrl is required");
builder.Services.AddHttpClient<IPlanClient, PlanClient>(c =>
{
    c.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(120);   // a patient can have thousands of plans (~13 MB)
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.All,
});

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/fhir/metadata"));
app.MapFhir();

app.Run();
