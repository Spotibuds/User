using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MongoDB.Bson;
using User.Services;
namespace User.Hubs;
[Authorize]
public class ChatHub(ChatCommands commands, IActiveChatTrackingService active) : Hub
{
    private string Actor => Input.Actor(Context.User!);
    public override async Task OnConnectedAsync() { await Groups.AddToGroupAsync(Context.ConnectionId, $"chat_user_{Actor}"); await base.OnConnectedAsync(); }
    public override async Task OnDisconnectedAsync(Exception? exception) { active.RemoveUserFromAllChats(Actor, Context.ConnectionId); await base.OnDisconnectedAsync(exception); }
    public Task JoinChat(string chatId) => JoinChatWithVisibility(chatId, true);
    public async Task JoinChatWithVisibility(string chatId, bool isVisible) { var chat = await commands.Member(Actor, chatId, Context.ConnectionAborted); await Groups.AddToGroupAsync(Context.ConnectionId, $"chat_{chat.Id}", Context.ConnectionAborted); SetActivity(chat.Id, isVisible); await Clients.Caller.SendAsync("ChatJoined", chat.Id, Context.ConnectionAborted); }
    // Visibility affects attention notices only. Personal message delivery stays subscribed.
    public async Task SetChatActive(string chatId, bool isVisible) { var chat = await commands.Member(Actor, chatId, Context.ConnectionAborted); SetActivity(chat.Id, isVisible); }
    private void SetActivity(string chatId, bool isVisible) { if (isVisible) active.AddUserToChat(chatId, Actor, Context.ConnectionId); else active.RemoveUserFromChat(chatId, Actor, Context.ConnectionId); }
    // Leaving only removes this connection's membership; it remains safe after chat deletion.
    public async Task LeaveChat(string chatId) { Input.ObjectId(chatId); chatId = ObjectId.Parse(chatId).ToString(); active.RemoveUserFromChat(chatId, Actor, Context.ConnectionId); await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"chat_{chatId}", Context.ConnectionAborted); }
    public async Task<object> SendMessage(string chatId, string content, string? clientMessageId = null) { var dto = await commands.Send(Actor, chatId, content, clientMessageId, cancellationToken: Context.ConnectionAborted); await Clients.Caller.SendAsync("MessageSent", dto, Context.ConnectionAborted); return dto; }
    public async Task MarkAsRead(string messageId) => await commands.Read(Actor, messageId, Context.ConnectionAborted);
    public async Task MarkMessageAsRead(string messageId) => await commands.Read(Actor, messageId, Context.ConnectionAborted);
    public async Task<object> MarkAllMessagesAsRead(string chatId) => await commands.ReadAll(Actor, chatId, cancellationToken: Context.ConnectionAborted);
    public async Task<object> MarkMessagesReadThrough(string chatId, string throughMessageId) => await commands.ReadAll(Actor, chatId, throughMessageId, Context.ConnectionAborted);
    public async Task StartTyping(string chatId) { var chat = await commands.Member(Actor, chatId, Context.ConnectionAborted); await Clients.OthersInGroup($"chat_{chat.Id}").SendAsync("UserTyping", new { chatId = chat.Id, userId = Actor, isTyping = true }, Context.ConnectionAborted); }
    public async Task StopTyping(string chatId) { var chat = await commands.Member(Actor, chatId, Context.ConnectionAborted); await Clients.OthersInGroup($"chat_{chat.Id}").SendAsync("UserTyping", new { chatId = chat.Id, userId = Actor, isTyping = false }, Context.ConnectionAborted); }
}
