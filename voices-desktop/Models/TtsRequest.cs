using System.Text.Json.Serialization;

namespace VoicesDesktop.Models;

public class TtsRequest
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("voice")]
    public string Voice { get; set; } = "Alba";

    [JsonPropertyName("speed")]
    public double Speed { get; set; } = 1.0;
}
