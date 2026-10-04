using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Driver;
using User.Data;
using User.Hubs;
using User.Services;

var builder = WebApplication.CreateBuilder(args);
string Required(string key) => !string.IsNullOrWhiteSpace(builder.Configuration[key]) ? builder.Configuration[key]! : throw new InvalidOperationException($"Required configuration missing: {key}");
var secret = Required("Jwt:Secret");
if (Encoding.UTF8.GetByteCount(secret) < 32) throw new InvalidOperationException("Jwt:Secret must contain at least 32 bytes.");
var issuer = Required("Jwt:Issuer");
var audience = Required("Jwt:Audience");
var origins = Required("Cors:AllowedOrigins").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
if (origins.Any(x => !Uri.TryCreate(x, UriKind.Absolute, out _) || x.Contains('*'))) throw new InvalidOperationException("Explicit CORS origins required.");
if (Encoding.UTF8.GetByteCount(Required("ServiceAuth:Secret")) < 32) throw new InvalidOperationException("ServiceAuth:Secret must contain at least 32 bytes.");
Required("AzureStorage:ConnectionString");
builder.Services.AddControllers(options => options.Filters.Add<ApiSafetyFilter>()).AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache(options => options.SizeLimit = 5000);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
        ValidIssuer = issuer, ValidAudience = audience, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
        ClockSkew = TimeSpan.FromSeconds(5), RequireExpirationTime = true, RequireSignedTokens = true, NameClaimType = ClaimTypes.NameIdentifier
    };
    options.Events = new JwtBearerEvents { OnMessageReceived = context =>
    {
        if (context.Request.Path.StartsWithSegments("/friend-hub") || context.Request.Path.StartsWithSegments("/chat-hub") || context.Request.Path.StartsWithSegments("/notification-hub")) context.Token = context.Request.Query["access_token"].FirstOrDefault();
        return Task.CompletedTask;
    }};
});
builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
var mongoSettings = MongoClientSettings.FromConnectionString(Required("ConnectionStrings:MongoDb"));
mongoSettings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
mongoSettings.ConnectTimeout = TimeSpan.FromSeconds(3);
mongoSettings.SocketTimeout = TimeSpan.FromSeconds(5);
mongoSettings.MaxConnectionPoolSize = 50;
builder.Services.AddSingleton<IMongoClient>(new MongoClient(mongoSettings));
builder.Services.AddSingleton(sp => new MongoDbContext(sp.GetRequiredService<IMongoClient>(), Required("MongoDB:DatabaseName")));
builder.Services.AddHttpClient("Identity", client => { client.BaseAddress = new Uri(Required("IdentityService:BaseUrl")); client.Timeout = TimeSpan.FromSeconds(2); client.DefaultRequestHeaders.Add("X-Spotibuds-Service", Required("ServiceAuth:Secret")); });
builder.Services.AddHttpClient("Music", client => { client.BaseAddress = new Uri(Required("MusicService:BaseUrl")); client.Timeout = TimeSpan.FromSeconds(5); });
builder.Services.AddScoped<ProfilePolicy>();
builder.Services.AddScoped<SessionValidator>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<NotificationCommands>();
builder.Services.AddScoped<SocialCommands>();
builder.Services.AddScoped<ChatCommands>();
builder.Services.AddSingleton<MongoTransactions>();
builder.Services.AddScoped<HistoryService>();
builder.Services.AddScoped<CanonicalSongReader>();
builder.Services.AddSingleton<IAzureBlobService, AzureBlobService>();
builder.Services.AddSingleton<IActiveChatTrackingService, ActiveChatTrackingService>();
builder.Services.AddSingleton<PresenceStore>();
builder.Services.AddSingleton<INowPlayingStore, NowPlayingStore>();
builder.Services.AddHostedService<IndexInitializer>();
builder.Services.AddSingleton<IndexState>();
builder.Services.AddHostedService<AvatarMaintenance>();
builder.Services.AddSignalR(options => { options.MaximumReceiveMessageSize = 16384; options.AddFilter<SessionHubFilter>(); });
builder.Services.AddCors(options => options.AddPolicy("Frontend", p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(context => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<DependencyErrors>();
app.UseMiddleware<SessionMiddleware>();
app.MapControllers();
app.MapHub<FriendHub>("/friend-hub", o => o.CloseOnAuthenticationExpiration = true);
app.MapHub<ChatHub>("/chat-hub", o => o.CloseOnAuthenticationExpiration = true);
app.MapHub<NotificationHub>("/notification-hub", o => o.CloseOnAuthenticationExpiration = true);
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).AllowAnonymous();
app.MapGet("/health/ready", async (MongoDbContext db, IAzureBlobService blobs, IndexState indexes, CancellationToken ct) =>
{
    try { if (!indexes.Ready) return Results.Json(new { status = "initializing" }, statusCode: 503); var topology = await db.Database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: ct); if (!topology.Contains("setName")) return Results.Json(new { status = "replica_set_required" }, statusCode: 503); await blobs.Ready(ct); return Results.Ok(new { status = "ready" }); }
    catch { return Results.Json(new { status = "unavailable" }, statusCode: 503); }
}).AllowAnonymous();
app.MapGet("/health", () => Results.Ok(new { status = "alive", readiness = "/health/ready" })).AllowAnonymous();
app.Run();
public partial class Program { }
