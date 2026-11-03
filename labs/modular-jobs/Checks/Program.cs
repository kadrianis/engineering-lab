using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModularJobs;

var count = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception($"FAIL: {label}");
    Console.WriteLine($"PASS: {label}");
    count++;
}
var store = new JobStore();
Check(store.Create("  diagnostic  ").Title == "diagnostic", "normalisation métier");
foreach (var title in new string?[] { null, "", "  ", new('x', 121) })
{
    try { store.Create(title); throw new Exception("Validation absente"); }
    catch (ArgumentException) { Check(true, "titre invalide rejeté"); }
}
var boundary = new JobStore();
var accepted = 0;
Parallel.For(0, 1100, _ =>
{
    try { boundary.Create("synthetic"); Interlocked.Increment(ref accepted); }
    catch (InvalidOperationException) { }
});
Check(accepted == 1000, "capacité atomique sous concurrence");

await using var app = ApiHost.Build(["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default", "Warning"]);
await app.StartAsync();
try
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(address) };
    Check((await client.GetAsync("/health")).StatusCode == HttpStatusCode.OK, "santé HTTP");
    var created = await client.PostAsJsonAsync("/jobs", new { title = "  sauvegarde  " });
    Check(created.StatusCode == HttpStatusCode.Created, "création HTTP 201");
    var job = (await created.Content.ReadFromJsonAsync<Job>())!;
    var found = await client.GetFromJsonAsync<Job>(created.Headers.Location);
    Check(found == job && job.Title == "sauvegarde" && job.Status == "pending", "parcours création/lecture et Location");
    Check((await client.GetAsync($"/jobs/{Guid.NewGuid()}")).StatusCode == HttpStatusCode.NotFound, "identifiant absent 404");
    Check((await client.GetAsync("/jobs/not-a-guid")).StatusCode == HttpStatusCode.NotFound, "route invalide 404");
    Check((await client.PostAsJsonAsync("/jobs", new { title = " " })).StatusCode == HttpStatusCode.BadRequest, "validation HTTP 400");
    Check((await client.PostAsync("/jobs", new StringContent("{", System.Text.Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.BadRequest, "JSON malformé 400");
    Check((await client.PostAsync("/jobs", new StringContent("{}"))).StatusCode == HttpStatusCode.UnsupportedMediaType, "média incorrect 415");
    Check((await client.PostAsJsonAsync("/jobs", new { title = new string('x', 5000) })).StatusCode == HttpStatusCode.RequestEntityTooLarge, "corps trop grand 413");
    var isolated = ApiHost.Build([]);
    Check(isolated.Services.GetRequiredService<JobStore>().Find(job.Id) is null, "nouvelle instance sans persistance");
    await isolated.DisposeAsync();
}
finally { await app.StopAsync(); }
// A long window avoids timing assertions: exactly two concurrent calls may enter.
await using var limited = ApiHost.Build(["--urls", "http://127.0.0.1:0",
    "--JobsRateLimit:PermitLimit", "2", "--JobsRateLimit:WindowSeconds", "3600",
    "--Logging:LogLevel:Default", "Warning"]);
await limited.StartAsync();
try
{
    var address = limited.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(address) };
    var requests = await Task.WhenAll(Enumerable.Range(0, 12)
        .Select(_ => client.PostAsJsonAsync("/jobs", new { title = "synthetic burst" })));
    Check(requests.Count(r => r.StatusCode == HttpStatusCode.Created) == 2, "deux créations admises sous concurrence");
    Check(requests.Count(r => (int)r.StatusCode == 429) == 10, "dix créations rejetées sans file d'attente");
    foreach (var response in requests.Where(r => (int)r.StatusCode == 429))
    {
        Check(response.Headers.RetryAfter?.Delta is { TotalSeconds: >= 1 and <= 3600 }, "Retry-After entier positif borné");
        Check(response.Headers.CacheControl?.NoStore == true, "429 non mis en cache");
        Check(response.Content.Headers.ContentType?.MediaType == "application/problem+json", "contrat ProblemDetails");
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Check(problem.GetProperty("status").GetInt32() == 429, "statut du corps 429");
    }
    Check((int)(await client.GetAsync($"/jobs/{Guid.NewGuid()}")).StatusCode == 429, "lectures et écritures partagent le budget");
    Check((await client.GetAsync("/health")).StatusCode == HttpStatusCode.OK, "santé disponible malgré saturation");
    foreach (var response in requests) response.Dispose();
}
finally { await limited.StopAsync(); }
foreach (var (key, value) in new[] { ("PermitLimit", "0"), ("PermitLimit", "10001"),
    ("WindowSeconds", "0"), ("WindowSeconds", "3601"), ("PermitLimit", "invalid") })
{
    try
    {
        await using var invalid = ApiHost.Build([$"--JobsRateLimit:{key}", value]);
        throw new Exception("Invalid rate limit was accepted");
    }
    catch (ArgumentOutOfRangeException) { Check(true, "configuration hors bornes refusée au démarrage"); }
    catch (InvalidOperationException) { Check(true, "configuration non entière refusée au démarrage"); }
}
Console.WriteLine($"SUCCESS: {count} checks");
