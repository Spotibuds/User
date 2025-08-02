using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;

namespace User.Services;

public interface IRabbitMqService
{
    Task PublishMessageAsync<T>(string routingKey, T message);
    void StartConsumingAsync(string queueName, Func<string, Task> messageHandler);
}

public class RabbitMqService : IRabbitMqService, IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;

    public RabbitMqService(IConfiguration configuration)
    {
        var factory = new ConnectionFactory()
        {
            HostName = configuration.GetConnectionString("RabbitMQ:Host") ?? "localhost",
            Port = int.Parse(configuration.GetConnectionString("RabbitMQ:Port") ?? "5672"),
            UserName = configuration.GetConnectionString("RabbitMQ:Username") ?? "guest",
            Password = configuration.GetConnectionString("RabbitMQ:Password") ?? "guest"
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();
    }

    public async Task PublishMessageAsync<T>(string routingKey, T message)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

        _channel.BasicPublish(exchange: "", routingKey: routingKey, basicProperties: null, body: body);

        await Task.CompletedTask;
    }

    public void StartConsumingAsync(string queueName, Func<string, Task> messageHandler)
    {
        _channel.QueueDeclare(queue: queueName, durable: true, exclusive: false, autoDelete: false, arguments: null);

        var consumer = new EventingBasicConsumer(_channel);
        consumer.Received += async (model, ea) =>
        {
            var body = ea.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);

            await messageHandler(message);

            _channel.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
        };

        _channel.BasicConsume(queue: queueName, autoAck: false, consumer: consumer);
    }

    public void Dispose()
    {
        _channel?.Close();
        _connection?.Close();
    }
}

// DTOs for RabbitMQ messages
public class ChatMessageNotification
{
    public string ChatId { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public List<string> Recipients { get; set; } = new();
}

public class FriendRequestNotification
{
    public string FriendshipId { get; set; } = string.Empty;
    public string RequesterId { get; set; } = string.Empty;
    public string AddresseeId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // "request", "accepted", "declined", "blocked"
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
} 