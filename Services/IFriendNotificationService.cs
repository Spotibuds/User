namespace User.Services;

public interface IFriendNotificationService
{
    Task NotifyFriendRequestSent(string userId, string targetUserId, string requesterUsername);
    Task NotifyFriendRequestAccepted(string userId, string friendId, string accepterUsername);
    Task NotifyFriendRequestDeclined(string userId, string friendId, string declinerUsername);
    Task NotifyFriendRemoved(string userId, string friendId, string removerUsername);
} 