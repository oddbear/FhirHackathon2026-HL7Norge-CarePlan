using PlanerFhir.Endpoints;
using PlanerFhir.Mapping;
using PlanerFhir.Upstream;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<FhirOptions>(builder.Configuration.GetSection("Fhir"));
builder.Services.AddSingleton<PlanMapper>();
builder.Services.AddHttpContextAccessor();

// Upstream:Mode = Mock (default, sample data) | Http (real Planer API at Upstream:BaseUrl)
if (string.Equals(builder.Configuration["Upstream:Mode"], "Http", StringComparison.OrdinalIgnoreCase))
{
    var baseUrl = builder.Configuration["Upstream:BaseUrl"]
        ?? throw new InvalidOperationException("Upstream:BaseUrl is required when Upstream:Mode=Http");
    builder.Services.AddHttpClient<IPlanClient, HttpPlanClient>(c =>
    {
        c.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromSeconds(120);   // a patient can have thousands of plans (~13 MB)
    }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });
}
else
{
    builder.Services.AddSingleton<IPlanClient, MockPlanClient>();
}

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/fhir/metadata"));
app.MapFhir();

app.Run();
