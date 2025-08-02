using Microsoft.AspNetCore.SignalR;
using User.Data;
using User.Hubs;
using MongoDB.Driver;
using MongoDB.Bson;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// MongoDB Configuration
builder.Services.AddSingleton<IMongoClient>(serviceProvider =>
{
    var connectionString = builder.Configuration.GetConnectionString("MongoDb")
        ?? Environment.GetEnvironmentVariable("ConnectionStrings__MongoDb");
    
    if (string.IsNullOrEmpty(connectionString))
    {
        return null;
    }
    
    // Fix authentication mechanism issue by removing authMechanism=DEFAULT
    if (!string.IsNullOrEmpty(connectionString) && connectionString.Contains("authMechanism=DEFAULT"))
    {
        connectionString = connectionString.Replace("authMechanism=DEFAULT", "authMechanism=SCRAM-SHA-1");
    }
    
    try
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(30);
        settings.ConnectTimeout = TimeSpan.FromSeconds(30);
        settings.SocketTimeout = TimeSpan.FromSeconds(30);
        settings.MaxConnectionPoolSize = 100;
        settings.MinConnectionPoolSize = 5;
        settings.MaxConnectionIdleTime = TimeSpan.FromMinutes(10);
        settings.MaxConnectionLifeTime = TimeSpan.FromMinutes(30);
        
        var client = new MongoClient(settings);
        
        // Test the connection
        try
        {
            client.GetDatabase("admin").RunCommandAsync((Command<BsonDocument>)"{ping:1}").Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            return null;
        }
        
        return client;
    }
    catch (Exception ex)
    {
        return null;
    }
});

builder.Services.AddScoped<MongoDbContext>(serviceProvider =>
{
    var mongoClient = serviceProvider.GetRequiredService<IMongoClient>();
    if (mongoClient == null)
    {
        return new MongoDbContext(null, "spotibuds");
    }
    return new MongoDbContext(mongoClient, "spotibuds");
});

// SignalR Configuration
builder.Services.AddSignalR();

// Register services
builder.Services.AddScoped<User.Services.IFriendNotificationService, User.Services.FriendNotificationService>();

// CORS Configuration
builder.Services.AddCors(options =>
{
    options.AddPolicy("SpotibudsPolicy", policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.WebHost.UseUrls("http://0.0.0.0:5002");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// CORS must come before other middleware
app.UseCors("SpotibudsPolicy");

// Add WebSocket support
app.UseWebSockets();

// Map Controllers and SignalR Hubs
app.MapControllers();

// Map SignalR Hubs
app.MapHub<FriendHub>("/friend-hub");

app.MapGet("/", () => "User API is running!");
app.MapGet("/health", () => new { status = "healthy", timestamp = DateTime.UtcNow });

// MongoDB health check endpoint
app.MapGet("/health/mongodb", async (MongoDbContext dbContext) =>
{
    try
    {
        if (!dbContext.IsConnected)
        {
            return Results.Problem(
                detail: "MongoDB is not connected",
                title: "MongoDB Connection Failed",
                statusCode: 503
            );
        }
        
        var database = dbContext.Users.Database;
        await database.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));
        return Results.Ok(new { status = "healthy", service = "mongodb", timestamp = DateTime.UtcNow });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            detail: ex.Message,
            title: "MongoDB Connection Failed",
            statusCode: 503
        );
    }
});

// Simple MongoDB connection test endpoint


app.Run(); 