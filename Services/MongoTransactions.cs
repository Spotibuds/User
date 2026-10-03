using MongoDB.Driver;
namespace User.Services;

// Every retry shares a ten-second budget. The demo requires an authenticated replica set.
public sealed class MongoTransactions(IMongoClient client)
{
    public async Task<T> Run<T>(Func<IClientSessionHandle, CancellationToken, Task<T>> operation, CancellationToken request = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var session = await client.StartSessionAsync(cancellationToken: timeout.Token);
            return await session.WithTransactionAsync(operation,
                new TransactionOptions(ReadConcern.Snapshot, ReadPreference.Primary, WriteConcern.WMajority, maxCommitTime: TimeSpan.FromSeconds(3)), timeout.Token);
        }
        catch (OperationCanceledException) when (!request.IsCancellationRequested)
        {
            throw new ApiProblem(503, "The database operation timed out. Retry after checking readiness.");
        }
    }
}
