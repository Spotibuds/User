using MongoDB.Driver;
using User.Data;
using User.Entities;
namespace User.Services;
public sealed class AvatarMaintenance(MongoDbContext db, IAzureBlobService blobs, ILogger<AvatarMaintenance> logger, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var intents = await db.AvatarCleanup.Find(x => x.DueAt <= clock.GetUtcNow().UtcDateTime && x.Attempts < 12).Limit(25).ToListAsync(ct);
                foreach (var job in intents)
                {
                    if (await db.Users.Find(x => x.AvatarUrl == job.Url).AnyAsync(ct)) { await blobs.Published(job.Url); continue; }
                    try { await blobs.Delete(job.Url); await blobs.Published(job.Url); }
                    catch (Exception ex) when (DependencyErrors.IsDependency(ex)) { await db.AvatarCleanup.UpdateOneAsync(x => x.Id == job.Id, Builders<AvatarCleanupIntent>.Update.Inc(x => x.Attempts, 1).Set(x => x.DueAt, clock.GetUtcNow().UtcDateTime.AddSeconds(Math.Min(300, 5 * Math.Pow(2, job.Attempts)))), cancellationToken: ct); }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogWarning("Avatar maintenance deferred: {ErrorType}", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}
