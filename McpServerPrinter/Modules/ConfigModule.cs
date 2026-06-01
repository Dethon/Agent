using Domain.Contracts;
using Domain.Tools.Printing;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Clients.Printer;
using Infrastructure.Printing;
using Infrastructure.Utils;
using McpServerPrinter.McpPrompts;
using McpServerPrinter.McpResources;
using McpServerPrinter.McpTools;
using McpServerPrinter.Services;
using McpServerPrinter.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpIpp;

namespace McpServerPrinter.Modules;

public static class ConfigModule
{
    public static PrinterSettings GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        return config.Get<PrinterSettings>()
               ?? throw new InvalidOperationException("Settings not found");
    }

    public static IServiceCollection ConfigurePrinter(this IServiceCollection services, PrinterSettings settings)
    {
        services
            .AddSingleton(settings)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ISharpIppClient>(_ => new SharpIppClient())
            .AddSingleton<IPrinterClient>(sp => new IppPrinterClient(
                sp.GetRequiredService<ISharpIppClient>(), new Uri(settings.PrinterUri), settings.DocumentFormat, settings.PrintScaling))
            .AddSingleton<IPrintSpool>(sp => new PrintSpool(settings.SpoolPath, sp.GetRequiredService<TimeProvider>()))
            .AddSingleton(sp => new PrintQueueCoordinator(
                sp.GetRequiredService<IPrintSpool>(),
                sp.GetRequiredService<IPrinterClient>(),
                sp.GetRequiredService<TimeProvider>(),
                TimeSpan.FromMilliseconds(settings.SubmitDebounceMilliseconds)))
            .AddSingleton<PrinterQueueFileSystem>()
            .AddHostedService<PrintSubmissionWorker>();

        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<FsReadTool>()
            .WithTools<FsInfoTool>()
            .WithTools<FsGlobTool>()
            .WithTools<FsSearchTool>()
            .WithTools<FsCreateTool>()
            .WithTools<FsEditTool>()
            .WithTools<FsDeleteTool>()
            .WithTools<FsCopyTool>()
            .WithTools<FsBlobReadTool>()
            .WithTools<FsBlobWriteTool>()
            .WithResources<FileSystemResource>()
            .WithPrompts<McpSystemPrompt>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                catch (Exception ex)
                {
                    var logger = context.Services?.GetRequiredService<ILogger<Program>>();
                    logger?.LogError(ex, "Error in {ToolName} tool", context.Params?.Name);
                    return ToolResponse.Create(ex);
                }
            }));

        return services;
    }
}