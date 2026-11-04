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
// Idempotency is atomic with job admission, bounded by the same 1000 job slots.
var keyedStore = new JobStore();
var sameKey = Guid.NewGuid();
var creations = new System.Collections.Concurrent.ConcurrentBag<Creation>();
Parallel.For(0, 100, _ => creations.Add(keyedStore.CreateOnce("  stable  ", sameKey)));
Check(creations.Count(c => !c.Replayed) == 1, "une seule admission concurrente par clé");
Check(creations.Select(c => c.Job.Id).Distinct().Count() == 1, "cent appels partagent un identifiant");
Check(keyedStore.CreateOnce("stable", sameKey).Replayed, "titre normalisé rejoué");
try { keyedStore.CreateOnce("different", sameKey); throw new Exception("Conflict absent"); }
catch (JobKeyConflictException) { Check(true, "conflit de contenu rejeté"); }
for (var i = 1; i < 1000; i++) keyedStore.Create("filler");
Check(keyedStore.CreateOnce("stable", sameKey).Replayed, "rejeu admis à capacité pleine");
try { keyedStore.CreateOnce("overflow", Guid.NewGuid()); throw new Exception("Capacity absent"); }
catch (InvalidOperationException) { Check(true, "nouvelle clé refusée à capacité pleine"); }
var unusedKey = Guid.NewGuid();
try { store.CreateOnce(" ", unusedKey); throw new Exception("Validation absent"); }
catch (ArgumentException) { Check(!store.CreateOnce("valid", unusedKey).Replayed, "échec validation ne réserve pas la clé"); }

await using var keyedApi = ApiHost.Build(["--urls", "http://127.0.0.1:0",
    "--JobsRateLimit:PermitLimit", "100", "--Logging:LogLevel:Default", "Warning"]);
await keyedApi.StartAsync();
try
{
    var address = keyedApi.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(address) };
    async Task<HttpResponseMessage> Send(string title, params string[] keys)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/jobs") { Content = JsonContent.Create(new { title }) };
        if (keys.Length > 0) request.Headers.TryAddWithoutValidation("X-Job-Request-Key", keys);
        return await client.SendAsync(request);
    }
    var key = Guid.NewGuid().ToString();
    var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Send("  export  ", key)));
    Check(responses.Count(r => r.StatusCode == HttpStatusCode.Created) == 1, "HTTP concurrence : un 201");
    Check(responses.Count(r => r.StatusCode == HttpStatusCode.OK) == 19, "HTTP concurrence : dix-neuf rejeux 200");
    var payloads = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<Job>()));
    Check(payloads.Distinct().Count() == 1, "HTTP rejeux : corps identiques");
    Check(responses.All(r => r.Headers.Location?.ToString() == $"/jobs/{payloads[0]!.Id}"), "Location identique sur 201 et 200");
    Check(responses.All(r => r.Headers.CacheControl?.NoStore == true), "créations et rejeux sans cache");
    using var conflict = await Send("secret conflicting title", key);
    Check(conflict.StatusCode == HttpStatusCode.Conflict, "HTTP conflit 409");
    var problem = await conflict.Content.ReadAsStringAsync();
    Check(!problem.Contains("secret") && !problem.Contains(key), "conflit ne divulgue ni titre ni clé");
    Check(conflict.Content.Headers.ContentType?.MediaType == "application/problem+json", "conflit ProblemDetails");
    using var replay = await Send("export", key.ToUpperInvariant());
    Check(replay.StatusCode == HttpStatusCode.OK, "casse GUID et normalisation titre acceptées");
    foreach (var bad in new[] { "", "not-a-guid", key + "," + key, "{" + key + "}" })
    {
        using var rejected = await Send("export", bad);
        Check(rejected.StatusCode == HttpStatusCode.BadRequest, "clé invalide 400");
    }
    using var multi = await Send("export", key, Guid.NewGuid().ToString());
    Check(multi.StatusCode == HttpStatusCode.BadRequest, "plusieurs clés refusées");
    using var a = await Send("export");
    using var b = await Send("export");
    Check(a.StatusCode == HttpStatusCode.Created && b.StatusCode == HttpStatusCode.Created, "sans clé : créations indépendantes");
    Check(a.Headers.Location != b.Headers.Location, "sans clé : identifiants distincts");
    using var other = await Send("export", Guid.NewGuid().ToString());
    Check(other.StatusCode == HttpStatusCode.Created && other.Headers.Location != replay.Headers.Location, "autre clé : nouvelle tâche");
    using var retryAfterConflict = await Send("export", key);
    Check(retryAfterConflict.StatusCode == HttpStatusCode.OK, "conflit ne remplace pas la première tâche");
    foreach (var response in responses) response.Dispose();
}
finally { await keyedApi.StopAsync(); }

Console.WriteLine($"SUCCESS: {count} checks");
