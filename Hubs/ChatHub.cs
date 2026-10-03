using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using User.Services;
namespace User.Hubs;
[Authorize]
public class ChatHub(ChatCommands commands, IActiveChatTrackingService active) : Hub
{
    private string Actor => Input.Actor(Context.User!);
    public override async Task OnConnectedAsync() { await Groups.AddToGroupAsync(Context.ConnectionId, $"chat_user_{Actor}"); await base.OnConnectedAsync(); }
    public override async Task OnDisconnectedAsync(Exception? exception) { active.RemoveUserFromAllChats(Actor, Context.ConnectionId); await base.OnDisconnectedAsync(exception); }
    public async Task JoinChat(string chatId) { await commands.Member(Actor, chatId); await Groups.AddToGroupAsync(Context.ConnectionId, $"chat_{chatId}"); active.AddUserToChat(chatId, Actor, Context.ConnectionId); await Clients.Caller.SendAsync("ChatJoined", chatId); }
    public async Task LeaveChat(string chatId) { await commands.Member(Actor, chatId); active.RemoveUserFromChat(chatId, Actor, Context.ConnectionId); await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"chat_{chatId}"); }
    public async Task<object> SendMessage(string chatId, string content, string? clientMessageId = null) { var dto = await commands.Send(Actor, chatId, content, clientMessageId); await Clients.Caller.SendAsync("MessageSent", dto); return dto; }
    public async Task MarkAsRead(string messageId) => await commands.Read(Actor, messageId);
    public async Task MarkMessageAsRead(string messageId) => await commands.Read(Actor, messageId);
    public async Task MarkAllMessagesAsRead(string chatId) => await commands.ReadAll(Actor, chatId);
    public async Task StartTyping(string chatId) { await commands.Member(Actor, chatId); await Clients.OthersInGroup($"chat_{chatId}").SendAsync("UserTyping", new { chatId, userId = Actor, isTyping = true }); }
    public async Task StopTyping(string chatId) { await commands.Member(Actor, chatId); await Clients.OthersInGroup($"chat_{chatId}").SendAsync("UserTyping", new { chatId, userId = Actor, isTyping = false }); }
}
