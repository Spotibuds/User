using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using User.Services;
namespace User.Hubs;
[Authorize]
public class NotificationHub(INotificationService notifications) : Hub
{
    private string Actor => Input.Actor(Context.User!);
    public override async Task OnConnectedAsync() { await Groups.AddToGroupAsync(Context.ConnectionId, $"notifications_{Actor}"); await Clients.Caller.SendAsync("UnreadCountUpdate", await notifications.GetUnreadCountAsync(Actor)); await base.OnConnectedAsync(); }
    public async Task GetNotifications(int limit = 20, int skip = 0) => await Clients.Caller.SendAsync("NotificationsLoaded", new { notifications = (await notifications.GetUserNotificationsAsync(Actor, limit, skip)).Select(NotificationService.Dto), unreadCount = await notifications.GetUnreadCountAsync(Actor) });
    public async Task MarkAsRead(string id) => await notifications.MarkAsReadAsync(id, Actor);
    public async Task MarkAsHandled(string id) => await notifications.MarkAsHandledAsync(id, Actor);
    public async Task MarkAllAsRead() => await notifications.MarkAllAsReadAsync(Actor);
}
