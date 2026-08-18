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
        public double VerifyPercent { get; set; } = 1.0; //da modificare a seconda dell'esigenza
        // Proprietà calcolata per identificare univocamente l'agente e il container
        public string UniqueKey => $"{AgentId}_{ContainerName}";
    }

    public class MqttSubscriberService : BackgroundService
    {
        private readonly ILogger<MqttSubscriberService> _logger;
        private readonly IConfiguration _config;
        private static readonly object _lockObject = new();

        public static Dictionary<string, MqttStatusMessage> ContainerStatuses { get; } = new();

        public MqttSubscriberService(ILogger<MqttSubscriberService> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var mqttFactory = new MqttClientFactory();
            using var mqttClient = mqttFactory.CreateMqttClient();

            var brokerHost = _config["MQTT:Broker"] ?? _config["MQTT:Host"] ?? "emqx";
            var brokerPort = int.Parse(_config["MQTT:Port"] ?? "1883");

            var mqttClientOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(brokerHost, brokerPort)
                .WithClientId("KopiaMonitorDashboard")
                .Build();

            mqttClient.ApplicationMessageReceivedAsync += e =>
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
                            // Usa la UniqueKey (AgentId + ContainerName) anziché solo ContainerName
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
                    if (!mqttClient.IsConnected)
                    {
                        await mqttClient.ConnectAsync(mqttClientOptions, stoppingToken);
                        _logger.LogInformation("Connesso al broker EMQX MQTT!");

                        var subscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
                            .WithTopicFilter(f => f.WithTopic("kopia/maintenance/#"))
                            .Build();

                        await mqttClient.SubscribeAsync(subscribeOptions, stoppingToken);
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
    }
}