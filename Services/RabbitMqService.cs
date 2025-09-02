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
    private readonly IConnection? _connection;
    private readonly IModel? _channel;
    private readonly bool _isConnected;

    public RabbitMqService(IConfiguration configuration)
    {
        try
        {
            var factory = new ConnectionFactory()
            {
                HostName = configuration["RabbitMQ:HostName"] ?? configuration["RabbitMQ:Host"] ?? "localhost",
                Port = int.Parse(configuration["RabbitMQ:Port"] ?? "5672"),
                UserName = configuration["RabbitMQ:UserName"] ?? configuration["RabbitMQ:Username"] ?? "guest",
                Password = configuration["RabbitMQ:Password"] ?? "guest",
                VirtualHost = configuration["RabbitMQ:VirtualHost"] ?? "/"
            };

            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();
            _isConnected = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RabbitMQ connection failed: {ex.Message}");
            _connection = null;
            _channel = null;
            _isConnected = false;
        }
    }

    public async Task PublishMessageAsync<T>(string routingKey, T message)
    {
        if (!_isConnected || _channel == null)
        {
            Console.WriteLine("RabbitMQ not connected, skipping message publish");
            await Task.CompletedTask;
            return;
        }

        try
        {
            // Declare durable exchange for friend events
            _channel.ExchangeDeclare("spotibuds.friends", ExchangeType.Topic, durable: true);
            
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
            var properties = _channel.CreateBasicProperties();
            properties.Persistent = true; // Make message persistent
            properties.MessageId = Guid.NewGuid().ToString();
            properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            
            _channel.BasicPublish(
                exchange: "spotibuds.friends",
                routingKey: routingKey,
                basicProperties: properties,
                body: body
            );
            
            Console.WriteLine($"Published message with routing key: {routingKey}");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to publish message to RabbitMQ: {ex.Message}");
            await Task.CompletedTask;
        }
    }

    public void StartConsumingAsync(string queueName, Func<string, Task> messageHandler)
    {
        if (!_isConnected || _channel == null)
        {
            Console.WriteLine("RabbitMQ not connected, skipping consumer setup");
            return;
        }

        try
        {
            _channel.QueueDeclare(queue: queueName, durable: true, exclusive: false, autoDelete: false, arguments: null);

            var consumer = new EventingBasicConsumer(_channel);
            consumer.Received += async (model, ea) =>
            {
                try
                {
                    var body = ea.Body.ToArray();
                    var message = Encoding.UTF8.GetString(body);
                    await messageHandler(message);
                    _channel.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing RabbitMQ message: {ex.Message}");
                    _channel.BasicNack(deliveryTag: ea.DeliveryTag, multiple: false, requeue: true);
                }
            };

            _channel.BasicConsume(queue: queueName, autoAck: false, consumer: consumer);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to setup RabbitMQ consumer: {ex.Message}");
        }
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