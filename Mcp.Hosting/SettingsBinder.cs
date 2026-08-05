using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace Mcp.Hosting;

// The one place any MCP server reads its configuration, so the four questions thirteen copies each
// answered for themselves have one answer each.
//
// Order is load-bearing: environment variables first, user secrets last, so a user secret outranks
// an environment variable. That is the reverse of the framework default and it is deliberate — see
// docs/adr/0005-user-secrets-outrank-environment-variables.md before touching it.
//
// The secrets id comes off the entry assembly rather than a Program type, because from here
// typeof(Program) would resolve to this assembly. Five servers have no UserSecretsId and the source
// is simply absent for them, which is exactly what they do today.
public static class SettingsBinder
{
    public static TSettings BindSettings<TSettings>(this IConfigurationBuilder configBuilder)
        where TSettings : class =>
        configBuilder.BindSettings<TSettings>(
            Assembly.GetEntryAssembly()?.GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId);

    internal static TSettings BindSettings<TSettings>(
        this IConfigurationBuilder configBuilder, string? userSecretsId)
        where TSettings : class
    {
        ArgumentNullException.ThrowIfNull(configBuilder);

        configBuilder.AddEnvironmentVariables();
        if (userSecretsId is not null)
        {
            configBuilder.AddUserSecrets(userSecretsId, reloadOnChange: false);
        }

        // Non-null even from completely empty configuration: the binder builds the instance and
        // leaves unbound members at their defaults. What a missing section really produces is a null
        // sub-record, which is what the walk below is for.
        var configuration = configBuilder.Build();
        var settings = configuration.Get<TSettings>()!;

        var missing = MissingRequiredMembers(settings, configuration, path: "", typeof(TSettings).Assembly).ToList();
        return missing.Count == 0
            ? settings
            : throw new InvalidOperationException(
                "Required configuration is missing: " + string.Join(", ", missing.Select(Describe)) + ".");
    }

    private static string Describe(string path) =>
        $"{path} (environment variable {EnvironmentVariableName(path)})";

    // Bots[0].BotToken lives in the environment as Bots__0__BotToken: an index or key is one more
    // path segment there, not bracket syntax.
    private static string EnvironmentVariableName(string path) =>
        path.Replace("[", "__", StringComparison.Ordinal)
            .Replace("]", "", StringComparison.Ordinal)
            .Replace(".", "__", StringComparison.Ordinal);

    // Null only, never empty. Three shipped servers carry required members that ship as "" and are
    // filled from secrets, and an empty optional key is how a feature is switched off; an
    // empty-is-invalid rule would refuse to start them.
    private static IEnumerable<string> MissingRequiredMembers(
        object instance, IConfiguration section, string path, Assembly settingsAssembly) =>
        instance.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0)
            .SelectMany(property => Inspect(property, instance, section, path, settingsAssembly));

    private static IEnumerable<string> Inspect(
        PropertyInfo property, object instance, IConfiguration section, string path, Assembly settingsAssembly)
    {
        var memberPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
        var value = property.GetValue(instance);

        if (value is null)
        {
            return IsRequired(property) ? [memberPath] : [];
        }

        // A required value type has no null to reveal an absent key — it binds to the type's
        // default and would sail past the null walk. Presence is therefore asked of the
        // configuration itself, so an explicit default written in config stays legal.
        if (property.PropertyType.IsValueType)
        {
            return IsRequired(property) && !section.GetSection(property.Name).Exists() ? [memberPath] : [];
        }

        // Recurses into a nested section, which is what a missing configuration block produces.
        if (IsSection(property.PropertyType, settingsAssembly))
        {
            return MissingRequiredMembers(value, section.GetSection(property.Name), memberPath, settingsAssembly);
        }

        // Collection elements are read at startup too: Telegram materialises BotRegistry(settings.Bots)
        // and voice SatelliteRegistry(settings.Satellites) at registration time, so a required member
        // missing from one element must fail startup by its indexed name instead of surfacing as a
        // null deep inside the server.
        return Elements(value)
            .Where(element => element.Value is not null && IsSection(element.Value.GetType(), settingsAssembly))
            .SelectMany(element => MissingRequiredMembers(
                element.Value!,
                section.GetSection(property.Name).GetSection(element.Key),
                $"{memberPath}[{element.Key}]",
                settingsAssembly));
    }

    private static IEnumerable<(string Key, object? Value)> Elements(object value) =>
        value switch
        {
            string => [],
            IDictionary dictionary => dictionary.Keys.Cast<object>()
                .Select(key => (key.ToString()!, dictionary[key])),
            IEnumerable enumerable => enumerable.Cast<object?>()
                .Select((element, index) => (index.ToString(), element)),
            _ => []
        };

    private static bool IsRequired(PropertyInfo property) =>
        property.IsDefined(typeof(RequiredMemberAttribute), inherit: false);

    private static bool IsSection(Type type, Assembly settingsAssembly) =>
        type.IsClass
        && type != typeof(string)
        && !typeof(IEnumerable).IsAssignableFrom(type)
        && type.Assembly == settingsAssembly;
}