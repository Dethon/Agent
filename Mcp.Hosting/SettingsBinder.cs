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
        var settings = configBuilder.Build().Get<TSettings>()!;

        var missing = MissingRequiredMembers(settings, path: "", typeof(TSettings).Assembly).ToList();
        return missing.Count == 0
            ? settings
            : throw new InvalidOperationException(
                "Required configuration is missing: " + string.Join(", ", missing.Select(Describe)) + ".");
    }

    private static string Describe(string path) =>
        $"{path} (environment variable {path.Replace(".", "__", StringComparison.Ordinal)})";

    // Null only, never empty. Three shipped servers carry required members that ship as "" and are
    // filled from secrets, and an empty optional key is how a feature is switched off; an
    // empty-is-invalid rule would refuse to start them.
    private static IEnumerable<string> MissingRequiredMembers(object instance, string path, Assembly settingsAssembly) =>
        instance.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0)
            .SelectMany(property => Inspect(property, instance, path, settingsAssembly));

    private static IEnumerable<string> Inspect(
        PropertyInfo property, object instance, string path, Assembly settingsAssembly)
    {
        var memberPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
        var value = property.GetValue(instance);

        if (value is null)
        {
            return property.IsDefined(typeof(RequiredMemberAttribute), inherit: false) ? [memberPath] : [];
        }

        // Recurses into a nested section, which is what a missing configuration block produces. A
        // collection's elements are data rather than sections, and no server reads one at startup.
        return IsSection(property.PropertyType, settingsAssembly)
            ? MissingRequiredMembers(value, memberPath, settingsAssembly)
            : [];
    }

    private static bool IsSection(Type type, Assembly settingsAssembly) =>
        type.IsClass
        && type != typeof(string)
        && !typeof(IEnumerable).IsAssignableFrom(type)
        && type.Assembly == settingsAssembly;
}