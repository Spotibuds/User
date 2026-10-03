using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Driver;
using User.Data;
namespace User.Services;
public sealed class ApiProblem(int status, string message) : Exception(message) { public int Status { get; } = status; }
public static class Input
{
    public static string Actor(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new ApiProblem(401, "Sign in required.");
    public static void ObjectId(string id) { if (!MongoDB.Bson.ObjectId.TryParse(id, out _)) throw new ApiProblem(400, "Invalid identifier."); }
    public static void GuidId(string id) { if (!Guid.TryParse(id, out _)) throw new ApiProblem(400, "Invalid account identifier."); }
    public static void Page(int limit, int skip) { if (limit is < 1 or > 100 || skip is < 0 or > 100000) throw new ApiProblem(400, "Pagination outside supported bounds."); }
    public static void Owner(ClaimsPrincipal actor, string target) { if (Actor(actor) != target) throw new ApiProblem(403, "This action belongs to another account."); }
    public static string Text(string? value, int max, bool required = false) { value = value?.Trim() ?? ""; if (value.Length > max || required && value.Length == 0) throw new ApiProblem(400, "Invalid text length."); return value; }
    public static bool Service(HttpContext context, IConfiguration config) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers["X-Spotibuds-Service"].ToString())), SHA256.HashData(Encoding.UTF8.GetBytes(config["ServiceAuth:Secret"] ?? "")));
}
public sealed class ApiSafetyFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        foreach (var value in context.ActionArguments)
        {
            if (value.Value is int number && (value.Key is "limit" or "pageSize") && (number is < 1 or > 100)) { context.Result = new BadRequestObjectResult(new { message = "limit must be between 1 and 100" }); return; }
            if (value.Value is int n && (value.Key is "skip" or "page") && (n < 0 || n > 100000 || value.Key == "page" && n == 0)) { context.Result = new BadRequestObjectResult(new { message = "Invalid pagination" }); return; }
        }
        await next();
    }
}
public sealed class DependencyErrors(RequestDelegate next)
{
    public static bool IsDependency(Exception ex) => ex is MongoException or TimeoutException or Azure.RequestFailedException or HttpRequestException or IOException or System.Net.Sockets.SocketException or TaskCanceledException || ex is AggregateException aggregate && aggregate.Flatten().InnerExceptions.All(IsDependency);
    public async Task Invoke(HttpContext context)
    {
        try { await next(context); }
        catch (Exception ex) when (!context.Response.HasStarted && (ex is ApiProblem || IsDependency(ex)))
        {
            context.Response.StatusCode = ex is ApiProblem problem ? problem.Status : ex is MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey } ? 409 : 503;
            await context.Response.WriteAsJsonAsync(new { message = ex is ApiProblem ? ex.Message : "A required local dependency is unavailable. Retry after checking readiness." });
        }
    }
}
public sealed class SessionValidator(IHttpClientFactory clients)
{
    public async Task Validate(ClaimsPrincipal user, CancellationToken ct)
    {
        var sid = user.FindFirstValue("sid");
        if (!Guid.TryParse(sid, out _)) throw new ApiProblem(401, "Session missing.");
        try { using var result = await clients.CreateClient("Identity").GetAsync($"api/auth/internal/sessions/{sid}", ct); if (result.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new ApiProblem(401, "Session revoked."); if (!result.IsSuccessStatusCode) throw new ApiProblem(503, "Identity service unavailable."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new ApiProblem(503, "Identity service unavailable."); }
    }
}
public sealed class SessionMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext context, SessionValidator validator)
    {
        if (context.User.Identity?.IsAuthenticated == true) await validator.Validate(context.User, context.RequestAborted);
        await next(context);
    }
}
public sealed class SessionHubFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try { await context.ServiceProvider.GetRequiredService<SessionValidator>().Validate(context.Context.User!, context.Context.ConnectionAborted); return await next(context); }
        catch (ApiProblem ex) { if (ex.Status == 401) context.Context.Abort(); throw new HubException(ex.Message); }
    }
}
public sealed class ProfilePolicy(MongoDbContext db)
{
    public async Task<Models.User> Find(string id, CancellationToken ct = default)
    {
        Models.User? user;
        if (Guid.TryParse(id, out _)) user = await db.Users.Find(x => x.IdentityUserId == id).FirstOrDefaultAsync(ct);
        else { Input.ObjectId(id); user = await db.Users.Find(x => x.Id == id).FirstOrDefaultAsync(ct); }
        return user ?? throw new ApiProblem(404, "Profile not found. Retry profile reconciliation.");
    }
    public static bool Visible(ClaimsPrincipal actor, Models.User profile) => !profile.IsPrivate || actor.IsInRole("Admin") || actor.FindFirstValue(ClaimTypes.NameIdentifier) == profile.IdentityUserId;
    public async Task<Models.User> Read(ClaimsPrincipal actor, string id, CancellationToken ct = default) { var profile = await Find(id, ct); if (!Visible(actor, profile)) throw new ApiProblem(403, "This profile is private."); return profile; }
    public async Task<Models.User> Own(ClaimsPrincipal actor, string id, CancellationToken ct = default) { var profile = await Find(id, ct); Input.Owner(actor, profile.IdentityUserId); return profile; }
}
