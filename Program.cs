using Microsoft.AspNetCore.SignalR;
using User.Data;
using User.Hubs;
using MongoDB.Driver;
using MongoDB.Bson;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// JWT Authentication Configuration
var jwtSecret = builder.Configuration["Jwt:Secret"];
var jwtIssuer = builder.Configuration["Jwt:Issuer"];
var jwtAudience = builder.Configuration["Jwt:Audience"];

Console.WriteLine($"JWT Configuration Debug:");
Console.WriteLine($"Jwt:Secret from config: {!string.IsNullOrEmpty(jwtSecret)}");
Console.WriteLine($"Jwt:Issuer from config: {jwtIssuer}");
Console.WriteLine($"Jwt:Audience from config: {jwtAudience}");
Console.WriteLine($"Final jwtSecret: {!string.IsNullOrEmpty(jwtSecret)}");
Console.WriteLine($"Final jwtIssuer: {jwtIssuer}");
Console.WriteLine($"Final jwtAudience: {jwtAudience}");

if (!string.IsNullOrEmpty(jwtSecret) && !string.IsNullOrEmpty(jwtIssuer) && !string.IsNullOrEmpty(jwtAudience))
{
    Console.WriteLine("JWT configuration found, setting up authentication...");
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromMinutes(5)
        };

        // Configure SignalR to use JWT authentication
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/friend-hub"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                Console.WriteLine($"JWT Authentication failed: {context.Exception.Message}");
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                Console.WriteLine($"JWT Token validated successfully for user: {context.Principal?.Identity?.Name}");
                return Task.CompletedTask;
            }
        };
    });
}
else
{
    Console.WriteLine("JWT configuration not found or incomplete. Authentication will not be available.");
}

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
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
});

// Register services
builder.Services.AddScoped<User.Services.IFriendNotificationService, User.Services.FriendNotificationService>();

// Register RabbitMQ service as optional - only if connection is available
builder.Services.AddSingleton<User.Services.IRabbitMqService>(serviceProvider =>
{
    try
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var rabbitMqService = new User.Services.RabbitMqService(configuration);
        return rabbitMqService;
    }
    catch (Exception ex)
    {
        // Log the error but don't fail the application startup
        Console.WriteLine($"RabbitMQ service initialization failed: {ex.Message}");
        // Return a null service or a mock implementation
        return null;
    }
});

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

builder.WebHost.UseUrls($"http://0.0.0.0:5002");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// CORS must come before other middleware
app.UseCors("SpotibudsPolicy");

// Add authentication and authorization
app.UseAuthentication();
app.UseAuthorization();

// Add WebSocket support
app.UseWebSockets();

// Map Controllers and SignalR Hubs
app.MapControllers();

// Map SignalR Hubs
app.MapHub<FriendHub>("/friend-hub", options =>
{
    options.CloseOnAuthenticationExpiration = true;
});

app.MapGet("/", () => "User API is running!");
app.MapGet("/health", () => new { status = "healthy", timestamp = DateTime.UtcNow });

// JWT Configuration test endpoint
app.MapGet("/jwt-config", (IConfiguration config) => new { 
    hasSecret = !string.IsNullOrEmpty(config["Jwt:Secret"]),
    hasIssuer = !string.IsNullOrEmpty(config["Jwt:Issuer"]),
    hasAudience = !string.IsNullOrEmpty(config["Jwt:Audience"]),
    secretLength = config["Jwt:Secret"]?.Length ?? 0,
    issuer = config["Jwt:Issuer"],
    audience = config["Jwt:Audience"]
});

// JWT Token validation test endpoint
app.MapGet("/test-jwt", (HttpContext context, IConfiguration config) =>
{
    var token = context.Request.Query["token"].FirstOrDefault();
    if (string.IsNullOrEmpty(token))
    {
        return Results.BadRequest("No token provided");
    }

    try
    {
        var tokenHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var jwtToken = tokenHandler.ReadJwtToken(token);
        
        return Results.Ok(new
        {
            isValid = true,
            claims = jwtToken.Claims.Select(c => new { c.Type, c.Value }).ToList(),
            issuer = jwtToken.Issuer,
            audience = jwtToken.Audiences.FirstOrDefault(),
            expires = jwtToken.ValidTo
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { isValid = false, error = ex.Message });
    }
});

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