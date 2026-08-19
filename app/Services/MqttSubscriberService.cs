using System.Text;
using System.Text.Json;
using MQTTnet;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace KopiaMonitorApp.Services
{
    public class MqttStatusMessage
    {
        public string AgentId { get; set; } = "UNKNOWN_AGENT";
        public string ContainerName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string LastMaintenance { get; set; } = string.Empty;
        public bool GarbageCollectorSuccess { get; set; } = true;
        public string Details { get; set; } = string.Empty;
        public string RawOutput { get; set; } = string.Empty;
        public bool VerifySuccess { get; set; } = true;
        public double VerifyPercent { get; set; } = 0.0001;
        public string UniqueKey => $"{AgentId}_{ContainerName}";
    }

    public class MqttSubscriberService : BackgroundService
    {
        private readonly ILogger<MqttSubscriberService> _logger;
        private readonly IConfiguration _config;
        private static readonly object _lockObject = new();
        
        private IMqttClient? _mqttClient;
        private MqttClientOptions? _mqttClientOptions;

        public static Dictionary<string, MqttStatusMessage> ContainerStatuses { get; } = new();

        public MqttSubscriberService(ILogger<MqttSubscriberService> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var mqttFactory = new MqttClientFactory();
            _mqttClient = mqttFactory.CreateMqttClient();

            var brokerHost = _config["MQTT:Broker"] ?? _config["MQTT:Host"] ?? "emqx";
            var brokerPort = int.Parse(_config["MQTT:Port"] ?? "1883");

            _mqttClientOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(brokerHost, brokerPort)
                .WithClientId("KopiaMonitorDashboard")
                .Build();

            _mqttClient.ApplicationMessageReceivedAsync += e =>
            {
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                _logger.LogInformation("Messaggio MQTT su {Topic}: {Payload}", e.ApplicationMessage.Topic, payload);

                try
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var statusData = JsonSerializer.Deserialize<MqttStatusMessage>(payload, options);
                    
                    if (statusData != null && !string.IsNullOrEmpty(statusData.ContainerName))
                    {
                        lock (_lockObject)
                        {
                            ContainerStatuses[statusData.UniqueKey] = statusData;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Errore durante il parsing del messaggio MQTT");
                }

                return Task.CompletedTask;
            };

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!_mqttClient.IsConnected)
                    {
                        await _mqttClient.ConnectAsync(_mqttClientOptions, stoppingToken);
                        _logger.LogInformation("Connesso al broker EMQX MQTT!");

                        var subscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
                            .WithTopicFilter(f => f.WithTopic("kopia/maintenance/#"))
                            .Build();

                        await _mqttClient.SubscribeAsync(subscribeOptions, stoppingToken);
                        _logger.LogInformation("Sottoscritto al topic 'kopia/maintenance/#'");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Connessione ad EMQX fallita: {Message}. Riprovo tra 5 secondi...", ex.Message);
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        public async Task PublishAsync(string topic, string payload)
        {
            if (_mqttClient == null || !_mqttClient.IsConnected)
            {
                throw new InvalidOperationException("Il client MQTT non è connesso al broker.");
            }

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .Build();

            await _mqttClient.PublishAsync(message);
            _logger.LogInformation("Pubblicato messaggio su {Topic}: {Payload}", topic, payload);
        }
    }
}