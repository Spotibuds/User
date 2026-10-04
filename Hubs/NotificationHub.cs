using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using User.Services;
namespace User.Hubs;
[Authorize]
public class NotificationHub(NotificationCommands notifications) : Hub
{
    private string Actor => SocialCommands.Account(Input.Actor(Context.User!));
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"notifications_{Actor}");
        await Clients.Caller.SendAsync("UnreadCountUpdate", await notifications.Unread(Actor));
        await base.OnConnectedAsync();
    }
    public async Task GetNotifications(int limit = 20, int skip = 0) => await Clients.Caller.SendAsync("NotificationsLoaded", await notifications.Snapshot(Actor, limit, skip));
    public Task<NotificationAcknowledgement> MarkAsRead(string id) => notifications.Read(id, Actor);
    public Task<NotificationAcknowledgement> MarkAsHandled(string id) => notifications.Read(id, Actor, handled: true);
    public Task<NotificationAcknowledgement> MarkAllAsRead() => notifications.ReadAll(Actor);
    public Task<NotificationAcknowledgement> MarkAllAsReadThrough(string throughId) => notifications.ReadAll(Actor, throughId);
    public Task<NotificationAcknowledgement> Dismiss(string id) => notifications.Dismiss(id, Actor);
}
