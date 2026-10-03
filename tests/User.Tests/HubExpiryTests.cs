using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace User.Tests;

public sealed class HubExpiryTests
{
    [Fact]
    public async Task ActualLongPollingConnectionClosesAtSignedAccessTokenExpiry()
    {
        using var factory = new UserFactory();
        await factory.InitializeAsync();
        var token = factory.Token(factory.Alice, expires: DateTime.UtcNow.AddSeconds(4));
        await using var hub = new HubConnectionBuilder().WithUrl("http://localhost/notification-hub", options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
        }).Build();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await hub.StartAsync();
        Assert.Equal(HubConnectionState.Connected, hub.State);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(HubConnectionState.Disconnected, hub.State);
    }
}
