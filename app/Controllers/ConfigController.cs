using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using KopiaMonitorApp.Services;

[ApiController]
[Route("api/config")]
public class ConfigController : ControllerBase
{
    private readonly MqttSubscriberService _mqttService;

    public ConfigController(MqttSubscriberService mqttService)
    {
        _mqttService = mqttService;
    }

    [HttpPost("advanced")]
    public async Task<IActionResult> SaveAdvancedConfig([FromBody] AdvancedConfigModel model)
    {
        try
        {
            var payloadObj = new
            {
                VerifyPercent = model.VerifyPercent,
                VerifyCron = string.IsNullOrWhiteSpace(model.VerifyCron) ? "0 2 * * *" : model.VerifyCron,
                MaintenanceCron = string.IsNullOrWhiteSpace(model.MaintenanceCron) ? "0 1 * * *" : model.MaintenanceCron
            };

            string jsonPayload = JsonSerializer.Serialize(payloadObj);

            await _mqttService.PublishAsync("kopia/config/advanced", jsonPayload, retain: true); // Imposta il flag retain a true

            return Ok(new { success = true, message = "Configurazione inviata con successo via MQTT agli agenti!" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = $"Errore invio MQTT: {ex.Message}" });
        }
    }
}

public class AdvancedConfigModel
{
    public decimal VerifyPercent { get; set; }
    public string? VerifyCron { get; set; }
    public string? MaintenanceCron { get; set; }
}