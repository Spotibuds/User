using Microsoft.AspNetCore.SignalR;
using User.Data;
using User.Hubs;
using MongoDB.Driver;
using MongoDB.Bson;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Security.Claims;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// JWT Configuration Configuration
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
            ClockSkew = TimeSpan.FromMinutes(10),  // Increased for more tolerance
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            NameClaimType = ClaimTypes.NameIdentifier,  // Ensure user ID is extracted properly
            // The following properties handle token encryption (which is not used in standard JWTs)
            TokenDecryptionKey = null,  // We're not using encrypted tokens
            TryAllIssuerSigningKeys = true  // Try all possible signing keys
        };
        
        // Additional settings to help with debugging
        options.IncludeErrorDetails = true;
        options.SaveToken = true;
        options.RequireHttpsMetadata = false;  // Set to true in production
        
        // Enhanced SignalR authentication with special handling for WebSockets
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                try {
                    // Check for token in query string (used by SignalR)
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    
                    // Special handling for SignalR hub connections
                    if (!string.IsNullOrEmpty(accessToken) && 
                        (path.StartsWithSegments("/friend-hub") || 
                         path.StartsWithSegments("/chat-hub")))
                    {
            // Extra debugging for negotiation paths which often have different requirements
            var tokenStr = accessToken.ToString();
            if (path.Value.Contains("/negotiate"))
            {
                Console.WriteLine($"SignalR negotiation request at {path} with token length: {tokenStr.Length}");
            }
            else 
            {
                Console.WriteLine($"SignalR connection attempt at {path} with token length: {tokenStr.Length}");
            }
            // Assign the token for validation
            context.Token = accessToken;
                    }
                    // If no query token, check Authorization header (used by API calls)
                    else if (context.Request.Headers.ContainsKey("Authorization"))
                    {
                        Console.WriteLine($"Authorization header found for path: {path}");
                    }
                    else if (path.StartsWithSegments("/friend-hub") || path.StartsWithSegments("/chat-hub")) 
                    {
                        Console.WriteLine($"⚠️ WARNING: SignalR connection without token at path: {path}");
                    }
                } 
                catch (Exception ex) {
                    Console.WriteLine($"Error in OnMessageReceived: {ex.Message}");
                    Console.WriteLine($"Stack trace: {ex.StackTrace}");
                }
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                var exception = context.Exception;
                var path = context.Request.Path;
                
                Console.WriteLine($"JWT Authentication failed for {path}: {exception.Message}");
                
                // Detailed error handling for different token failure scenarios
                if (exception is SecurityTokenExpiredException)
                {
                    Console.WriteLine("Token expired");
                    context.Response.Headers.Append("Token-Expired", "true");
                }
                else if (exception is SecurityTokenInvalidSignatureException)
                {
                    Console.WriteLine("Invalid token signature");
                }
                else if (exception is SecurityTokenInvalidIssuerException)
                {
                    Console.WriteLine($"Invalid issuer: {((SecurityTokenInvalidIssuerException)exception).InvalidIssuer}");
                }
                else if (exception.Message.Contains("IDX10612"))
                {
                    // This specific error happens when the system tries to decrypt a token that isn't encrypted
                    // For SignalR WebSocket connections, we'll bypass this error
                    if (path.StartsWithSegments("/friend-hub") || path.StartsWithSegments("/chat-hub"))
                    {
                        // Check if it's a WebSocket request
                        if (context.HttpContext.WebSockets.IsWebSocketRequest)
                        {
                            Console.WriteLine("Handling WebSocket connection with JWT token - bypassing decryption check");
                            // For WebSocket connections, we'll manually validate the token
                            try {
                                var token = context.Request.Query["access_token"].ToString();
                                var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                                var jsonToken = handler.ReadToken(token) as System.IdentityModel.Tokens.Jwt.JwtSecurityToken;
                                
                                // If we get here, the token is at least readable
                                Console.WriteLine($"Successfully read JWT token for WebSocket. Subject: {jsonToken.Subject}");
                                
                                // Set success response for WebSockets
                                context.NoResult();
                                return Task.CompletedTask;
                            }
                            catch (Exception ex) {
                                Console.WriteLine($"Manual token validation failed: {ex.Message}");
                            }
                        }
                    }
                }
                
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                var userId = context.Principal?.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
                Console.WriteLine($"Token successfully validated for user: {userId}");
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                Console.WriteLine($"Authentication challenge triggered for: {context.Request.Path}");
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
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);       // Increased from 30 to 60
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.MaximumReceiveMessageSize = 102400; // 100 KB
    options.HandshakeTimeout = TimeSpan.FromSeconds(30);            // Increased from 15 to 30
    options.StreamBufferCapacity = 20;                              // Better for streaming scenarios
})
.AddJsonProtocol(options =>
{
    // Use camelCase for properties to match JavaScript conventions
    options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    // Preserve references to handle circular references
    options.PayloadSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.Preserve;
});// Register services
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
var corsSection = builder.Configuration.GetSection("Cors");
var allowedOrigins = corsSection["AllowedOrigins"];

Console.WriteLine($"CORS Configuration Debug:");
Console.WriteLine($"Cors:AllowedOrigins from config: '{allowedOrigins}'");
Console.WriteLine($"Environment ASPNETCORE_ENVIRONMENT: {builder.Environment.EnvironmentName}");

// Also try reading directly from environment variable
var envCorsOrigins = Environment.GetEnvironmentVariable("Cors__AllowedOrigins");
Console.WriteLine($"Cors__AllowedOrigins from environment: '{envCorsOrigins}'");

// Use environment variable if config is empty
if (string.IsNullOrEmpty(allowedOrigins) && !string.IsNullOrEmpty(envCorsOrigins))
{
    allowedOrigins = envCorsOrigins;
    Console.WriteLine($"Using CORS origins from environment variable: '{allowedOrigins}'");
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        Console.WriteLine($"Setting up CORS policy with origins: '{allowedOrigins}'");
        
        if (!string.IsNullOrEmpty(allowedOrigins))
        {
            if (allowedOrigins == "*")
            {
                Console.WriteLine("CORS: Wildcard origin detected, allowing all origins without credentials for development");
                policy.AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .WithExposedHeaders("Content-Disposition");
            }
            else
            {
                var origins = allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries);
                Console.WriteLine($"CORS: Allowing specific origins: {string.Join(", ", origins)}");
                policy.WithOrigins(origins)
                    .AllowAnyMethod()
                    .AllowAnyHeader()
                    .AllowCredentials() // Required for SignalR
                    .WithExposedHeaders("Content-Disposition");
            }
        }
        else
        {
            Console.WriteLine("CORS: No origins configured, blocking all requests for security");
            // For security, if no origins are configured, we shouldn't allow any origins
            // This forces proper configuration through environment variables
            policy.WithOrigins() // Empty origins array - blocks all cross-origin requests
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials()
                .WithExposedHeaders("Content-Disposition");
        }
    });
});

builder.WebHost.UseUrls($"http://0.0.0.0:80");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// CORS must come before other middleware
app.UseCors("AllowFrontend");

// Add authentication and authorization
app.UseAuthentication();
app.UseAuthorization();

// Add WebSocket support
app.UseWebSockets();

// Map Controllers and SignalR Hubs
app.MapControllers();

// Map SignalR Hubs with configuration
app.MapHub<FriendHub>("/friend-hub", options =>
{
    options.CloseOnAuthenticationExpiration = false; // Changed to false to prevent disconnects on token expiration
    // We have token refresh logic in the client
    
    // Support all transport types for maximum compatibility
    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets | 
                        Microsoft.AspNetCore.Http.Connections.HttpTransportType.ServerSentEvents |
                        Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                        
    // Increased timeout values
    options.LongPolling.PollTimeout = TimeSpan.FromSeconds(90);
    
    // WebSockets configuration - simpler configuration to avoid issues
    options.WebSockets.CloseTimeout = TimeSpan.FromSeconds(30);
    
    // Accept any WebSocket subprotocol offered by the client
    options.WebSockets.SubProtocolSelector = protocolList => 
        (protocolList != null && protocolList.Count > 0) ? protocolList[0] : null;
});

app.MapHub<ChatHub>("/chat-hub", options =>
{
    options.CloseOnAuthenticationExpiration = false; // Changed to false to prevent disconnects on token expiration
    // We have token refresh logic in the client
    
    // Support all transport types for maximum compatibility
    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets | 
                        Microsoft.AspNetCore.Http.Connections.HttpTransportType.ServerSentEvents |
                        Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                        
    // Increased timeout values
    options.LongPolling.PollTimeout = TimeSpan.FromSeconds(90);
    
    // WebSockets configuration - simpler configuration to avoid issues
    options.WebSockets.CloseTimeout = TimeSpan.FromSeconds(30);
    
    // Accept any WebSocket subprotocol offered by the client
    options.WebSockets.SubProtocolSelector = protocolList => 
        (protocolList != null && protocolList.Count > 0) ? protocolList[0] : null;
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

// SignalR diagnostic endpoint
app.MapGet("/diagnose-signalr", (HttpContext context) => 
{
    var request = context.Request;
    
    // Get all information that might help diagnose SignalR issues
    var diagnosticInfo = new
    {
        remoteIp = context.Connection.RemoteIpAddress?.ToString(),
        protocol = request.Protocol,
        scheme = request.Scheme,
        method = request.Method,
        path = request.Path.ToString(),
        queryString = request.QueryString.ToString(),
        headers = request.Headers.Select(h => new { h.Key, Value = h.Value.ToString() }).ToList(),
        cookies = request.Cookies.Select(c => new { c.Key, c.Value }).ToList(),
        webSocketsSupported = context.WebSockets.IsWebSocketRequest,
        authenticationStatus = context.User.Identity?.IsAuthenticated ?? false,
        userIdentifier = context.User.Identity?.Name,
        claims = context.User.Claims.Select(c => new { c.Type, c.Value }).ToList(),
        serverDateTime = DateTime.UtcNow
    };
    
    return Results.Ok(diagnosticInfo);
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

// CORS configuration test endpoint
app.MapGet("/cors-config", (IConfiguration config) => new { 
    corsFromConfig = config.GetSection("Cors")["AllowedOrigins"],
    corsFromEnv = Environment.GetEnvironmentVariable("Cors__AllowedOrigins"),
    environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
    allEnvVars = Environment.GetEnvironmentVariables()
        .Cast<System.Collections.DictionaryEntry>()
        .Where(x => x.Key.ToString().Contains("Cors"))
        .ToDictionary(x => x.Key.ToString(), x => x.Value.ToString())
});

app.Run(); 