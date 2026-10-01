using System.Text.Json.Serialization;

namespace Tamp.Observer.RawBucket.Abstractions;

/// <summary>
/// The envelope the Go landing exporter writes alongside each raw payload (ADR 0003). Property names
/// match the exporter's JSON. Shared by every raw-bucket provider (file spool, Valkey).
/// </summary>
public sealed class RawEnvelope
{
    [JsonPropertyName("receipt_id")] public string ReceiptId { get; set; } = "";
    [JsonPropertyName("received_at")] public DateTimeOffset ReceivedAt { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("signal")] public string Signal { get; set; } = "";
    [JsonPropertyName("format")] public string? Format { get; set; }
    [JsonPropertyName("payload_file")] public string PayloadFile { get; set; } = "";
    [JsonPropertyName("payload_bytes")] public int PayloadBytes { get; set; }
}
