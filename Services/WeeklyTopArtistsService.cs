using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using System.Threading;
using System.Threading.Tasks;
using User.Data;
using User.Models;

namespace User.Services;

public class WeeklyTopArtistsService : BackgroundService
{
    private readonly ILogger<WeeklyTopArtistsService> _logger;
    private readonly IServiceProvider _serviceProvider;

    public WeeklyTopArtistsService(ILogger<WeeklyTopArtistsService> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = GetDelayUntilNextSundayUtc();
                await Task.Delay(delay, stoppingToken);

                await RecomputeAllUsersTopArtists(stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // ignore on shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in WeeklyTopArtistsService loop");
                // Wait a minute before retrying to avoid tight loop on persistent errors
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private static TimeSpan GetDelayUntilNextSundayUtc()
    {
        var now = DateTime.UtcNow;
        // Compute upcoming Sunday 00:00 UTC
        int daysUntilSunday = ((int)DayOfWeek.Sunday - (int)now.DayOfWeek + 7) % 7;
        var nextSunday = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(daysUntilSunday);
        if (nextSunday <= now)
        {
            nextSunday = nextSunday.AddDays(7);
        }
        return nextSunday - now;
    }

    private async Task RecomputeAllUsersTopArtists(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoDbContext>();
        if (!context.IsConnected || context.Users == null)
        {
            _logger.LogWarning("MongoDB not connected; skipping weekly top artists computation");
            return;
        }

        var users = await context.Users.Find(_ => true).ToListAsync(ct);
        var weekStart = GetCurrentWeekStartUtc();
        foreach (var user in users)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var weekItems = user.ListeningHistory
                    .Where(h => h.PlayedAt >= weekStart)
                    .ToList();

                var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in weekItems)
                {
                    if (string.IsNullOrWhiteSpace(item.Artist)) continue;
                    var parts = item.Artist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var p in parts)
                    {
                        var key = p.Trim();
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        if (!dict.ContainsKey(key)) dict[key] = 0;
                        dict[key]++;
                    }
                }

                var top = dict
                    .Select(kv => new TopArtist { Name = kv.Key, Count = kv.Value })
                    .OrderByDescending(t => t.Count)
                    .ThenBy(t => t.Name)
                    .Take(3)
                    .ToList();

                var update = Builders<User.Models.User>.Update
                    .Set(u => u.TopArtistsWeekStart, weekStart)
                    .Set(u => u.TopArtistsCurrentWeek, top);

                await context.Users.UpdateOneAsync(u => u.Id == user.Id, update, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed computing top artists for user {UserId}", user.Id);
            }
        }

        _logger.LogInformation("Weekly top artists computed for {Count} users", users.Count);
    }

    private static DateTime GetCurrentWeekStartUtc()
    {
        var now = DateTime.UtcNow;
        int diff = (int)now.DayOfWeek; // Sunday=0
        return new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-diff);
    }
}
