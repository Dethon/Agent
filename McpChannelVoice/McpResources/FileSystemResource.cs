using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpResources;

[McpServerResourceType]
public class FileSystemResource
{
    [McpServerResource(UriTemplate = "filesystem://timers", Name = "Timers Filesystem", MimeType = "application/json")]
    [Description("Voice countdown-timer control surface")]
    public string GetInfo() => JsonSerializer.Serialize(new
    {
        name = "timers",
        mountPoint = "/timers",
        description = "Short countdown timers that ring on the voice satellites. Arm one by creating /timers/<descriptive-id>/timer.json with JSON {durationSeconds, text?, target} — target is {satelliteId | satelliteIds | room | all}; default it to the speaking room. Read /timers/<id>/status.json for remainingSeconds/firesAt; cancel by deleting /timers/<id>. Timers are immutable (delete and recreate) and fire once, ringing tone + message until dismissed by wake word/button or capped. Use the HA alarms calendar for clock-time alarms/reminders, not timers."
    });
}