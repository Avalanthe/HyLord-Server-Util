using System;
using System.Text.Json.Serialization;

namespace HyLordServerUtil
{
    public class BanEntry
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "infinite";

        [JsonPropertyName("target")]
        public string Target { get; set; } = "";

        [JsonPropertyName("by")]
        public string By { get; set; } = "";

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";

        [JsonIgnore]
        public string TimestampLocal =>
            Timestamp > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(Timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : "";
        [JsonIgnore]
        public string DisplayName { get; set; } = "(unknown)";
    }
}
