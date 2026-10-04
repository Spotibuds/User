using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using User.Entities;
using Xunit;

namespace User.Tests;

public sealed class NotificationCleanupTests
{
    private static HttpClient CleanupClient(UserFactory factory)
    {
        var client = factory.Client();
        client.DefaultRequestHeaders.Add("X-Spotibuds-Service", factory.Services.GetRequiredService<IConfiguration>()["ServiceAuth:Secret"]);
        return client;
    }

    [Fact]
    public async Task AccountCleanupRemovesParentGraphAndGroupChatNoticesAndRefreshesSurvivingRecipient()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync();
        var people = await factory.Db.Users.Find(_ => true).ToListAsync();
        var alice = people.Single(x => x.IdentityUserId == factory.Alice);
        var bob = people.Single(x => x.IdentityUserId == factory.Bob);
        var mallory = people.Single(x => x.IdentityUserId == factory.Mallory);
        var chat = new Chat { IsGroup = true, Participants = [alice.Id, bob.Id, mallory.Id] };
        await factory.Db.Chats.InsertOneAsync(chat);
        await factory.Db.Messages.InsertOneAsync(new Message { ChatId = chat.Id, SenderId = bob.Id, Content = "group message" });
        await factory.Db.Friends.InsertOneAsync(new Friend { UserId = alice.Id, FriendId = bob.Id, PairKey = "cleanup-pair" });
        await factory.Db.Follows.InsertOneAsync(new FollowEdge { FollowerId = factory.Bob, FollowedId = factory.Alice });
        var post = new FeedItem { IdentityUserId = factory.Alice, Type = "recent_song", SongId = UserFactory.Song };
        await factory.Db.Feed.InsertOneAsync(post);
        await factory.Db.History.InsertOneAsync(new HistoryEvent { IdentityUserId = factory.Alice, SongId = UserFactory.Song });
        await factory.Db.Reactions.InsertOneAsync(new Reaction { FromIdentityUserId = factory.Bob, ToIdentityUserId = factory.Alice, PostId = post.Id, Emoji = "❤️" });
        var groupNotice = new Notification { SourceUserId = factory.Bob, TargetUserId = factory.Mallory, Type = NotificationType.Message, Data = new() { ["chatId"] = chat.Id } };
        var ownNotice = new Notification { SourceUserId = factory.Alice, TargetUserId = factory.Bob, Type = NotificationType.Follow };
        var unrelated = new Notification { SourceUserId = factory.Bob, TargetUserId = factory.Mallory, Type = NotificationType.Other };
        await factory.Db.Notifications.InsertManyAsync([groupNotice, ownNotice, unrelated]);
        await using var hub = factory.Hub("/notification-hub", factory.Mallory);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<object>("NotificationsChanged", _ => changed.TrySetResult());
        await hub.StartAsync();
        using var cleanup = CleanupClient(factory);
        Assert.Equal(HttpStatusCode.NoContent, (await cleanup.DeleteAsync($"/api/users/internal/{factory.Alice}")).StatusCode);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await factory.Db.Users.CountDocumentsAsync(x => x.IdentityUserId == factory.Alice));
        Assert.Equal(0, await factory.Db.Chats.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await factory.Db.Messages.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await factory.Db.Friends.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await factory.Db.Follows.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await factory.Db.Feed.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await factory.Db.History.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await factory.Db.Reactions.CountDocumentsAsync(_ => true));
        Assert.Equal(unrelated.Id, (await factory.Db.Notifications.Find(_ => true).SingleAsync()).Id);
        using var recipient = factory.Client(factory.Mallory);
        var snapshot = await recipient.GetStringAsync($"/api/notifications/{factory.Mallory}");
        Assert.Contains(unrelated.Id, snapshot); Assert.DoesNotContain(groupNotice.Id, snapshot);
        Assert.Equal(HttpStatusCode.NoContent, (await cleanup.DeleteAsync($"/api/users/internal/{factory.Alice}")).StatusCode);
        Assert.Equal(2, await factory.Db.Users.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task AccountCleanupRetainsProfileUntilBoundedChatBatchesFinish()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync();
        var alice = await factory.Db.Users.Find(x => x.IdentityUserId == factory.Alice).SingleAsync();
        var bob = await factory.Db.Users.Find(x => x.IdentityUserId == factory.Bob).SingleAsync();
        var chats = Enumerable.Range(0, 101).Select(_ => new Chat { IsGroup = true, Participants = [alice.Id, bob.Id] }).ToArray();
        await factory.Db.Chats.InsertManyAsync(chats);
        await factory.Db.Messages.InsertManyAsync(chats.Select(chat => new Message { ChatId = chat.Id, SenderId = bob.Id, Content = "bounded batch" }));
        await factory.Db.Notifications.InsertManyAsync(chats.Select(chat => new Notification { SourceUserId = factory.Bob, TargetUserId = factory.Mallory, Type = NotificationType.Message, Data = new() { ["chatId"] = chat.Id } }));
        using var cleanup = CleanupClient(factory);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await cleanup.DeleteAsync($"/api/users/internal/{factory.Alice}")).StatusCode);
        Assert.Equal(1, await factory.Db.Users.CountDocumentsAsync(x => x.IdentityUserId == factory.Alice));
        Assert.Equal(1, await factory.Db.Chats.CountDocumentsAsync(_ => true));
        Assert.Equal(1, await factory.Db.Messages.CountDocumentsAsync(_ => true));
        Assert.Equal(1, await factory.Db.Notifications.CountDocumentsAsync(_ => true));
        Assert.Equal(HttpStatusCode.NoContent, (await cleanup.DeleteAsync($"/api/users/internal/{factory.Alice}")).StatusCode);
        Assert.Equal(0, await factory.Db.Users.CountDocumentsAsync(x => x.IdentityUserId == factory.Alice));
        Assert.Equal(0, await factory.Db.Chats.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await factory.Db.Messages.CountDocumentsAsync(_ => true));
        Assert.Equal(0, await factory.Db.Notifications.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task UnauthorizedOrFailedParentCleanupPreservesProfileAndNotification()
    {
        using var factory = new UserFactory(); await factory.InitializeAsync();
        var notice = new Notification { SourceUserId = factory.Alice, TargetUserId = factory.Bob, Type = NotificationType.Other };
        await factory.Db.Notifications.InsertOneAsync(notice);
        using var ordinary = factory.Client(factory.Alice);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ordinary.DeleteAsync($"/api/users/internal/{factory.Alice}")).StatusCode);
        factory.Dependencies.PlaylistDeleteStatus = HttpStatusCode.ServiceUnavailable;
        using var cleanup = CleanupClient(factory);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await cleanup.DeleteAsync($"/api/users/internal/{factory.Alice}")).StatusCode);
        Assert.Equal(1, await factory.Db.Users.CountDocumentsAsync(x => x.IdentityUserId == factory.Alice));
        Assert.Equal(notice.Id, (await factory.Db.Notifications.Find(_ => true).SingleAsync()).Id);
        factory.Dependencies.PlaylistDeleteStatus = HttpStatusCode.OK;
        Assert.Equal(HttpStatusCode.NoContent, (await cleanup.DeleteAsync($"/api/users/internal/{factory.Alice}")).StatusCode);
        Assert.Equal(0, await factory.Db.Notifications.CountDocumentsAsync(_ => true));
    }
}
