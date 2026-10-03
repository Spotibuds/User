using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Services;
namespace User.Controllers;
[ApiController, Route("api/notifications")]
public class NotificationsController(MongoDbContext db, INotificationService notifications) : ControllerBase
{
    [HttpGet("{userId}")] public async Task<object> Get(string userId, int limit = 50, int skip = 0) { Input.Owner(User, userId); var list = await notifications.GetUserNotificationsAsync(userId, limit, skip); return new { notifications = list.Select(NotificationService.Dto), totalCount = await db.Notifications.CountDocumentsAsync(n => n.TargetUserId == userId && (n.ExpiresAt == null || n.ExpiresAt > DateTime.UtcNow)), unreadCount = await notifications.GetUnreadCountAsync(userId) }; }
    [HttpPost("{id}/read")] public async Task<object> Read(string id) { await notifications.MarkAsReadAsync(id, Input.Actor(User)); return new { message = "Notification read" }; }
    [HttpPost("{id}/handle")] public async Task<object> Handle(string id) { await notifications.MarkAsHandledAsync(id, Input.Actor(User)); return new { message = "Notification handled" }; }
    [HttpPost("{userId}/read-all")] public async Task<object> ReadAll(string userId) { Input.Owner(User, userId); await notifications.MarkAllAsReadAsync(userId); return new { message = "Notifications read" }; }
    [HttpDelete("{userId}/cleanup")] public async Task<object> Cleanup(string userId, int daysOld = 30) { Input.Owner(User, userId); await notifications.CleanupOldNotificationsAsync(userId, daysOld); return new { message = "Notifications cleaned" }; }
    [HttpDelete("{id}")] public async Task<object> Delete(string id, string? userId = null) { Input.ObjectId(id); var actor = Input.Actor(User); var result = await db.Notifications.DeleteOneAsync(x => x.Id == id && x.TargetUserId == actor); if (result.DeletedCount == 0) throw new ApiProblem(404, "Notification not found."); return new { message = "Notification deleted" }; }
    [HttpDelete("{userId}/all")] public async Task<object> DeleteAll(string userId) { Input.Owner(User, userId); await db.Notifications.DeleteManyAsync(x => x.TargetUserId == userId); return new { message = "Notifications deleted" }; }
}
