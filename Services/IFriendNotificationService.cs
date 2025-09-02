namespace User.Services;

public interface IFriendNotificationService
{
    Task NotifyFriendRequestSent(string userId, string targetUserId, string requesterUsername, string friendshipId);
    Task NotifyFriendRequestAccepted(string userId, string friendId, string accepterUsername);
    Task NotifyFriendRequestDeclined(string userId, string friendId, string declinerUsername);
    Task NotifyFriendAdded(string userId, string friendId, string friendUsername);
    Task NotifyFriendRemoved(string userId, string friendId, string removerUsername);
    Task NotifyFriendRemovedByYou(string userId, string removedFriendId, string removedUsername);
} 