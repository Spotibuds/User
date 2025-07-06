using MongoDB.Driver;
using User.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IMongoClient>(serviceProvider =>
{
    var connectionString = builder.Configuration.GetConnectionString("MongoDB");
    return new MongoClient(connectionString);
});

builder.Services.AddScoped<MongoDbContext>(serviceProvider =>
{
    var mongoClient = serviceProvider.GetRequiredService<IMongoClient>();
    return new MongoDbContext(mongoClient, "spotibuds");
});

var corsSection = builder.Configuration.GetSection("Cors");
var allowedOrigins = corsSection["AllowedOrigins"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("SpotibudsPolicy", policy =>
    {
        if (allowedOrigins == "*")
        {
            policy.AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
        else
        {
            var origins = allowedOrigins?.Split(',') ?? Array.Empty<string>();
            policy.WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        }
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("SpotibudsPolicy");
app.UseAuthorization();
app.MapControllers();

app.MapGet("/", () => "User API is running!");
app.MapGet("/health", () => new { status = "healthy", timestamp = DateTime.UtcNow });

app.Run(); 