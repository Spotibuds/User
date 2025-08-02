using Microsoft.AspNetCore.SignalR;

namespace User.Hubs;

public class FriendHubSimple : Hub
{
    public FriendHubSimple()
    {
        Console.WriteLine("FriendHubSimple constructor called");
    }

    public override async Task OnConnectedAsync()
    {
        Console.WriteLine($"FriendHubSimple OnConnectedAsync called for connection: {Context.ConnectionId}");
        
        // Get userId from query string
        var userId = Context.GetHttpContext()?.Request.Query["id"].FirstOrDefault();
        
        Console.WriteLine($"Query parameters - userId: {userId}");
        
        if (!string.IsNullOrEmpty(userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
            Console.WriteLine($"User {userId} connected to friend hub simple: {Context.ConnectionId}");
        }
        else
        {
            Console.WriteLine($"Client connected without userId: {Context.ConnectionId}");
        }
        
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.GetHttpContext()?.Request.Query["id"].FirstOrDefault();
        
        if (!string.IsNullOrEmpty(userId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user_{userId}");
            Console.WriteLine($"User {userId} disconnected from friend hub simple: {Context.ConnectionId}");
        }
        else
        {
            Console.WriteLine($"Client disconnected from friend hub simple: {Context.ConnectionId}");
        }
        
        await base.OnDisconnectedAsync(exception);
    }

    // Test method to verify hub is working
    public async Task TestConnection()
    {
        Console.WriteLine("FriendHubSimple TestConnection method called");
        await Clients.Caller.SendAsync("TestResponse", "FriendHubSimple is working!");
    }
} 