using System.Collections.Concurrent;

namespace ModularJobs;

public sealed record Job(Guid Id, string Title, string Status);
public sealed record CreateJob(string? Title);
public sealed record Creation(Job Job, bool Replayed);
public sealed class JobKeyConflictException : Exception;

public sealed class JobStore
{
    private readonly ConcurrentDictionary<Guid, Job> jobs = new();
    private readonly Dictionary<Guid, Job> requests = new();
    public Job Create(string? title) => CreateOnce(title, null).Job;

    public Creation CreateOnce(string? title, Guid? requestKey)
    {
        var normalized = title?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 120)
            throw new ArgumentException("Le titre doit contenir de 1 à 120 caractères.");
        // Bound memory for this disposable demonstration. Admission must be atomic.
        lock (jobs)
        {
            // Replay precedes capacity admission: an existing request needs no new slot.
            if (requestKey is { } key && requests.TryGetValue(key, out var previous))
            {
                if (previous.Title != normalized) throw new JobKeyConflictException();
                return new Creation(previous, true);
            }
            if (jobs.Count >= 1000) throw new InvalidOperationException("Capacité du lab atteinte.");
            var job = new Job(Guid.NewGuid(), normalized, "pending");
            jobs[job.Id] = job;
            if (requestKey is { } newKey) requests.Add(newKey, job);
            return new Creation(job, false);
        }
    }
    public Job? Find(Guid id) => jobs.GetValueOrDefault(id);
}
