using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using User.Services;
namespace User.Controllers;
[ApiController, Route("api/notifications")]
public class NotificationsController(NotificationCommands commands) : ControllerBase
{
    private string Actor => SocialCommands.Account(Input.Actor(User));
    private string Owner(string userId) { userId = SocialCommands.Account(userId); if (userId != Actor) throw new ApiProblem(403, "This action belongs to another account."); return userId; }
    [HttpGet("{userId}")] public Task<NotificationSnapshot> Get(string userId, int limit = 50, int skip = 0, string? before = null) => commands.Snapshot(Owner(userId), limit, skip, before);
    [HttpGet("{userId}/unread-count")] public async Task<object> Count(string userId) => new { unreadCount = await commands.Unread(Owner(userId)) };
    [HttpPost("{id}/read")] public Task<NotificationAcknowledgement> Read(string id) => commands.Read(id, Actor);
    [HttpPost("{id}/handle")] public Task<NotificationAcknowledgement> Handle(string id) => commands.Read(id, Actor, handled: true);
    [HttpPost("{userId}/read-all")] public Task<NotificationAcknowledgement> ReadAll(string userId, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] NotificationBoundary? boundary = null) => commands.ReadAll(Owner(userId), boundary?.ThroughId);
    [HttpDelete("{userId}/cleanup")] public Task<NotificationAcknowledgement> Cleanup(string userId, int daysOld = 30) => commands.Cleanup(Owner(userId), daysOld);
    [HttpDelete("{id}")] public Task<NotificationAcknowledgement> Delete(string id, string? userId = null) { if (userId != null) Owner(userId); return commands.Dismiss(id, Actor); }
    [HttpDelete("{userId}/all")] public Task<NotificationAcknowledgement> DeleteAll(string userId, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] NotificationBoundary? boundary = null) => commands.DismissAll(Owner(userId), boundary?.ThroughId);
}
