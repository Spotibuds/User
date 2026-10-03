using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using User.Data;
using User.Services;

namespace User.Tests;

public sealed class UserFactory : WebApplicationFactory<Program>
{
    private readonly string? storageConnection;
    private readonly Func<IAzureBlobService, IAzureBlobService>? blobDecorator;
    public UserFactory(string? storageConnection = null, Func<IAzureBlobService, IAzureBlobService>? blobDecorator = null) { this.storageConnection = storageConnection; this.blobDecorator = blobDecorator; }
    public const string Secret = "disposable-user-test-jwt-secret-at-least-32-bytes";
    public const string Song = "111111111111111111111111";
    public string DatabaseName { get; } = "spotibuds_test_" + Guid.NewGuid().ToString("N");
    public string Alice { get; } = Guid.NewGuid().ToString();
    public string Bob { get; } = Guid.NewGuid().ToString();
    public string Mallory { get; } = Guid.NewGuid().ToString();
    public ControlledClock Clock { get; } = new();
    public DependencyHandler Dependencies { get; } = new();
    private bool cleaned;
    public MongoDbContext Db => Services.GetRequiredService<MongoDbContext>();
    static UserFactory()
    {
        var mongo = Environment.GetEnvironmentVariable("USER_TEST_MONGO") ?? throw new InvalidOperationException("USER_TEST_MONGO must point to the isolated disposable local Mongo server; tests never fall back to existing databases");
        foreach (var pair in new Dictionary<string, string>
        {
            ["ConnectionStrings__MongoDb"] = mongo, ["MongoDB__DatabaseName"] = "unused_test_factory",
            ["Jwt__Secret"] = Secret, ["Jwt__Issuer"] = "user-tests", ["Jwt__Audience"] = "user-tests",
            ["Cors__AllowedOrigins"] = "http://127.0.0.1:3100", ["ServiceAuth__Secret"] = "test-service-secret-at-least-32-bytes",
            ["AzureStorage__ConnectionString"] = "UseDevelopmentStorage=true", ["AzureStorage__PublicBaseUrl"] = "http://127.0.0.1:5103",
            ["IdentityService__BaseUrl"] = "http://identity.invalid", ["MusicService__BaseUrl"] = "http://music.invalid"
        }) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = "*", ["Logging:LogLevel:Default"] = "Warning" }));
        builder.ConfigureLogging(logging => { logging.ClearProviders(); logging.AddConsole(); });
        builder.ConfigureServices(services =>
        {
            var mongoSettings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("USER_TEST_MONGO"));
            mongoSettings.DirectConnection = true;
            mongoSettings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
            services.RemoveAll<IMongoClient>(); services.AddSingleton<IMongoClient>(new MongoClient(mongoSettings));
            services.RemoveAll<MongoDbContext>(); services.AddSingleton(sp => new MongoDbContext(sp.GetRequiredService<IMongoClient>(), DatabaseName));
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
            services.AddHttpClient("Identity").ConfigurePrimaryHttpMessageHandler(() => Dependencies);
            services.AddHttpClient("Music").ConfigurePrimaryHttpMessageHandler(() => Dependencies);
            if (storageConnection is not null)
            {
                services.RemoveAll<IAzureBlobService>();
                services.AddSingleton<IAzureBlobService>(sp =>
                {
                    var blobs = new AzureBlobService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["AzureStorage:ConnectionString"] = storageConnection,
                        ["AzureStorage:PublicBaseUrl"] = "http://127.0.0.1:5103"
                    }).Build(), sp.GetRequiredService<MongoDbContext>(), sp.GetRequiredService<TimeProvider>());
                    return blobDecorator?.Invoke(blobs) ?? blobs;
                });
            }
        });
    }
    public async Task InitializeAsync()
    {
        await Db.Database.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));
        await Db.Users.InsertManyAsync(new[]
        {
            new Models.User { IdentityUserId = Alice, UserName = "alice", DisplayName = "Alice", Bio = "original", Roles = ["User"] },
            new Models.User { IdentityUserId = Bob, UserName = "bob", DisplayName = "Bob", Roles = ["User"] },
            new Models.User { IdentityUserId = Mallory, UserName = "mallory", DisplayName = "Mallory", Roles = ["User"] }
        });
        // Await the actual hosted index initializer before concurrency assertions.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var cursor = await Db.Follows.Indexes.ListAsync();
            if ((await cursor.ToListAsync()).Count > 1) return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException("Disposable Mongo indexes did not initialize");
    }
    public string Token(string actor, string role = "User", string? signingSecret = null, string issuer = "user-tests", string audience = "user-tests", DateTime? expires = null, bool includeSid = true)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor), new(ClaimTypes.Name, actor), new(ClaimTypes.Role, role) };
        if (includeSid) claims.Add(new("sid", Guid.NewGuid().ToString()));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience, claims,
            notBefore: DateTime.UtcNow.AddMinutes(-10), expires: expires ?? DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingSecret ?? Secret)), SecurityAlgorithms.HmacSha256)));
    }
    public HttpClient Client(string? actor = null, string role = "User", string? token = null)
    {
        var client = CreateClient(); if (actor != null || token != null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Token(actor!, role)); return client;
    }
    public HubConnection Hub(string path, string actor) => new HubConnectionBuilder().WithUrl("http://localhost" + path, options =>
    {
        options.Transports = HttpTransportType.LongPolling; options.AccessTokenProvider = () => Task.FromResult<string?>(Token(actor));
        options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
    }).Build();
    protected override void Dispose(bool disposing)
    {
        if (disposing && !cleaned && DatabaseName.StartsWith("spotibuds_test_", StringComparison.Ordinal))
        {
            cleaned = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Services.GetRequiredService<IMongoClient>().DropDatabase(DatabaseName, timeout.Token);
        }
        base.Dispose(disposing);
    }
}

public class ControlledClock : TimeProvider
{
    public DateTimeOffset Value { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Value;
}
public class DependencyHandler : HttpMessageHandler
{
    public HttpStatusCode SessionStatus { get; set; } = HttpStatusCode.NoContent;
    public HttpStatusCode SongStatus { get; set; } = HttpStatusCode.OK;
    public string Artist { get; set; } = "Test artist";
    public int SongDuration { get; set; } = 125;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.Contains("/sessions/")) return Task.FromResult(new HttpResponseMessage(SessionStatus));
        if (request.RequestUri.AbsolutePath.Contains("/api/songs/"))
        {
            if (request.RequestUri.AbsolutePath.Split('/').Last() != UserFactory.Song) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(SongStatus) { Content = new StringContent(JsonSerializer.Serialize(new { id = UserFactory.Song, title = "Test song", durationSec = SongDuration, artists = new[] { new { name = Artist } }, coverUrl = (string?)null }), Encoding.UTF8, "application/json") });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }
}
