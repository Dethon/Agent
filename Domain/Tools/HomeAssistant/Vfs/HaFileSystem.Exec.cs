using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Exceptions;

namespace Domain.Tools.HomeAssistant.Vfs;

public sealed partial class HaFileSystem
{
    public async Task<JsonNode> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
    {
        var catalog = await catalogProvider.GetAsync(ct);
        var cwd = HaVfsPath.Parse(path);
        if (cwd.Kind != HaVfsKind.EntityDir || catalog.EntityById(cwd.EntityId!) is null)
        {
            return ExecResult(127, "", $"Not an entity directory: {path}. cd into /ha/entities/<class>/<id> first.");
        }

        var tokens = ShellTokenize(command);
        var entityId = cwd.EntityId!;
        var actions = HaActionResolver.ServicesFor(entityId, catalog.Services);
        var available = string.Join(", ", actions.Select(a => $"{a.Service}.sh"));

        if (tokens.Count == 0)
        {
            return ExecResult(127, "", $"No command. Available actions: {available}");
        }

        var script = tokens[0].StartsWith("./", StringComparison.Ordinal) ? tokens[0][2..] : tokens[0];
        if (!script.EndsWith(".sh", StringComparison.Ordinal))
        {
            return ExecResult(127, "", $"command not found: {tokens[0]}. This filesystem only runs action files. Available actions: {available}");
        }

        var serviceName = script[..^3];
        var svc = actions.FirstOrDefault(a => a.Service.Equals(serviceName, StringComparison.Ordinal));
        if (svc is null)
        {
            return ExecResult(127, "", $"command not found: {script}. Available actions: {available}");
        }

        var args = tokens.Skip(1).ToList();
        if (args.Contains("--help") || args.Contains("-h"))
        {
            return ExecResult(0, HaServiceHelpRenderer.Render(entityId, svc), "");
        }

        JsonObject data;
        try
        {
            data = HaArgParser.Parse(args, svc);
        }
        catch (ArgumentException ex)
        {
            return ExecResult(2, "", ex.Message);
        }

        try
        {
            IReadOnlyDictionary<string, JsonNode?> payload = data.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.DeepClone());
            var result = await clientFactory().CallServiceAsync(svc.Domain, svc.Service, entityId, payload, ct);
            var changed = new JsonArray(result.ChangedEntities
                .Select(e => (JsonNode?)$"{e.EntityId} → {e.State}").ToArray());
            var stdout = new JsonObject { ["ok"] = true, ["changed"] = changed };
            if (result.Response is not null)
            {
                stdout["response"] = result.Response.DeepClone();
            }
            return ExecResult(0, stdout.ToJsonString(), "");
        }
        catch (HomeAssistantException ex)
        {
            return ExecResult(1, "",
                $"{ex.Message}\nRe-check the field types with `{serviceName}.sh --help`; don't retry the same shape.");
        }
    }

    private static JsonObject ExecResult(int exitCode, string stdout, string stderr) => new()
    {
        ["stdout"] = stdout,
        ["stderr"] = stderr,
        ["exitCode"] = exitCode,
        ["truncated"] = false
    };

    // Minimal shell tokeniser: whitespace-split, honouring single and double quotes so JSON
    // object values like --advanced '{"eco":true}' survive as one token.
    private static List<string> ShellTokenize(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        var has = false;

        foreach (var c in command)
        {
            if (quote != '\0')
            {
                if (c == quote)
                { quote = '\0'; }
                else
                { current.Append(c); }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                has = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (has)
                { tokens.Add(current.ToString()); current.Clear(); has = false; }
            }
            else
            {
                current.Append(c);
                has = true;
            }
        }
        if (has)
        { tokens.Add(current.ToString()); }
        return tokens;
    }
}