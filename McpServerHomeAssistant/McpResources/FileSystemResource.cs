using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpResources;

[McpServerResourceType]
public class FileSystemResource
{
    [McpServerResource(
        UriTemplate = "filesystem://ha",
        Name = "Home Assistant Filesystem",
        MimeType = "application/json")]
    [Description("Home Assistant entities and controls as a virtual filesystem")]
    public string GetHaInfo()
    {
        return JsonSerializer.Serialize(new
        {
            name = "ha",
            mountPoint = "/ha",
            description = "Home Assistant as a filesystem. Browse `/ha/entities/<class>/<id>/` or `/ha/areas/<room>/<entity_id>/`. `read state.yaml` for live state; `read <service>.sh` (or `exec '<service>.sh --help'`) for an action's arguments; `exec '<service>.sh --flag value'` to control a device. NOT a shell — exec only runs the listed *.sh action files (anything else returns exit 127). No create/edit/delete."
        });
    }
}