namespace User.Services;
public interface IActiveChatTrackingService
{
    void AddUserToChat(string chatId, string userId, string connectionId);
    void RemoveUserFromChat(string chatId, string userId, string connectionId);
    void RemoveUserFromAllChats(string userId, string connectionId);
    bool IsUserInChat(string chatId, string userId);
    HashSet<string> GetUsersInChat(string chatId);
}
public sealed class ActiveChatTrackingService : IActiveChatTrackingService
{
    private readonly object gate = new();
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> chats = new();
    public void AddUserToChat(string chatId, string userId, string connectionId) { lock (gate) { if (!chats.TryGetValue(chatId, out var users)) chats[chatId] = users = new(); if (!users.TryGetValue(userId, out var set)) users[userId] = set = []; set.Add(connectionId); } }
    public void RemoveUserFromChat(string chatId, string userId, string connectionId) { lock (gate) { if (!chats.TryGetValue(chatId, out var users) || !users.TryGetValue(userId, out var set)) return; set.Remove(connectionId); if (set.Count == 0) users.Remove(userId); if (users.Count == 0) chats.Remove(chatId); } }
    public void RemoveUserFromAllChats(string userId, string connectionId) { lock (gate) foreach (var chatId in chats.Keys.ToList()) RemoveUserFromChat(chatId, userId, connectionId); }
    public bool IsUserInChat(string chatId, string userId) { lock (gate) return chats.TryGetValue(chatId, out var users) && users.ContainsKey(userId); }
    public HashSet<string> GetUsersInChat(string chatId) { lock (gate) return chats.TryGetValue(chatId, out var users) ? new(users.Keys) : []; }
}
