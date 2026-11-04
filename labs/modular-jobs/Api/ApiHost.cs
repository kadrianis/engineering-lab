using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ModularJobs;

public static class ApiHost
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
        builder.Services.AddSingleton<JobStore>();
        // One shared budget for job reads AND writes, per process. No user identity implied.
        var permits = builder.Configuration.GetValue("JobsRateLimit:PermitLimit", 60);
        var seconds = builder.Configuration.GetValue("JobsRateLimit:WindowSeconds", 60);
        if (permits is < 1 or > 10000 || seconds is < 1 or > 3600)
            throw new ArgumentOutOfRangeException("JobsRateLimit", "Permits: 1..10000; window seconds: 1..3600");
        builder.Services.AddRateLimiter(options =>
        {
            options.AddFixedWindowLimiter("jobs", limiter =>
            {
                limiter.PermitLimit = permits;
                limiter.Window = TimeSpan.FromSeconds(seconds);
                limiter.QueueLimit = 0;
                limiter.AutoReplenishment = true;
            });
            options.OnRejected = async (context, token) =>
            {
                // Round upward: clients must never be asked to retry before a permit can renew.
                var delay = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
                    ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)) : seconds;
                context.HttpContext.Response.Headers.RetryAfter = delay.ToString(CultureInfo.InvariantCulture);
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                await Results.Problem(statusCode: 429, title: "Job request budget exhausted")
                    .ExecuteAsync(context.HttpContext);
            };
        });
        var app = builder.Build();
        app.UseRateLimiter();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapPost("/jobs", (CreateJob input, JobStore store, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            Guid? requestKey = null;
            if (context.Request.Headers.TryGetValue("X-Job-Request-Key", out var values))
            {
                if (values.Count != 1 || !Guid.TryParseExact(values[0], "D", out var parsed))
                    return Results.Problem(statusCode: 400, title: "Expected one GUID request key");
                requestKey = parsed;
            }
            try
            {
                var result = store.CreateOnce(input.Title, requestKey);
                var job = result.Job;
                if (result.Replayed)
                {
                    context.Response.Headers.Location = $"/jobs/{job.Id}";
                    return Results.Ok(job);
                }
                return Results.Created($"/jobs/{job.Id}", job);
            }
            catch (JobKeyConflictException)
            {
                return Results.Problem(statusCode: 409, title: "Request key already used with another title");
            }
            catch (ArgumentException error)
            {
                return Results.Problem(statusCode: 400, title: error.Message);
            }
            catch (InvalidOperationException error)
            {
                return Results.Problem(statusCode: 503, title: error.Message);
            }
        }).RequireRateLimiting("jobs");
        app.MapGet("/jobs/{id:guid}", (Guid id, JobStore store) =>
            store.Find(id) is { } job ? Results.Ok(job) : Results.NotFound())
            .RequireRateLimiting("jobs");
        return app;
    }
}
