using System.Text.Json;

namespace User.Services;

public class RabbitMqEventService : IRabbitMqEventService
{
    private readonly IRabbitMqService _rabbitMqService;
    private readonly ILogger<RabbitMqEventService> _logger;

    public RabbitMqEventService(IRabbitMqService rabbitMqService, ILogger<RabbitMqEventService> logger)
    {
        _rabbitMqService = rabbitMqService;
        _logger = logger;
    }

    public async Task PublishFriendEventAsync(string eventType, object eventData)
    {
        try
        {
            // Use routing key pattern: friend.{eventType}
            var routingKey = $"friend.{eventType}";
            
            await _rabbitMqService.PublishMessageAsync(routingKey, eventData);
            
            _logger.LogInformation($"Published friend event: {eventType}");
        }
        catch (Exception ex)
        {
            // Don't fail the main operation if event publishing fails
            _logger.LogError(ex, $"Failed to publish friend event: {eventType}");
        }
    }
}
