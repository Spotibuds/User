using System.Collections.Concurrent;

namespace User.Services;

/// <summary>
/// Service to track which users are currently viewing which chats
/// </summary>
public interface IActiveChatTrackingService
{
    void AddUserToChat(string chatId, string userId);
    void RemoveUserFromChat(string chatId, string userId);
    void RemoveUserFromAllChats(string userId);
    bool IsUserInChat(string chatId, string userId);
    HashSet<string> GetUsersInChat(string chatId);
}

public class ActiveChatTrackingService : IActiveChatTrackingService
{
    // Thread-safe dictionary to track active chat users
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _activeChatUsers = new();

    public void AddUserToChat(string chatId, string userId)
    {
        _activeChatUsers.AddOrUpdate(
            chatId,
            new ConcurrentDictionary<string, bool> { [userId] = true },
            (key, existing) =>
            {
                existing[userId] = true;
                return existing;
            }
        );
    }

    public void RemoveUserFromChat(string chatId, string userId)
    {
        if (_activeChatUsers.TryGetValue(chatId, out var users))
        {
            users.TryRemove(userId, out _);
            
            // Clean up empty chat entries
            if (users.IsEmpty)
            {
                _activeChatUsers.TryRemove(chatId, out _);
            }
        }
    }

    public void RemoveUserFromAllChats(string userId)
    {
        var chatsToRemove = new List<string>();
        
        foreach (var kvp in _activeChatUsers)
        {
            if (kvp.Value.TryRemove(userId, out _) && kvp.Value.IsEmpty)
            {
                chatsToRemove.Add(kvp.Key);
            }
        }
        
        foreach (var chatId in chatsToRemove)
        {
            _activeChatUsers.TryRemove(chatId, out _);
        }
    }

    public bool IsUserInChat(string chatId, string userId)
    {
        return _activeChatUsers.TryGetValue(chatId, out var users) && users.ContainsKey(userId);
    }

    public HashSet<string> GetUsersInChat(string chatId)
    {
        if (_activeChatUsers.TryGetValue(chatId, out var users))
        {
            return new HashSet<string>(users.Keys);
        }
        return new HashSet<string>();
    }
}
