using Domain.Contracts;
using Domain.Tools.Printing;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Clients.Printer;
using Infrastructure.Printing;
using Infrastructure.Utils;
using McpServerPrinter.McpPrompts;
using McpServerPrinter.McpResources;
using McpServerPrinter.Services;
using McpServerPrinter.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpIpp;

namespace McpServerPrinter.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigurePrinter(this IServiceCollection services, PrinterSettings settings)
    {
        services
            .AddSingleton(settings)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ISharpIppClient>(_ => new SharpIppClient())
            .AddSingleton<IPrinterClient>(sp => new IppPrinterClient(
                sp.GetRequiredService<ISharpIppClient>(), new Uri(settings.PrinterUri), settings.DocumentFormat, settings.PrintScaling))
            .AddSingleton<IPrintSpool>(sp => new PrintSpool(settings.SpoolPath, sp.GetRequiredService<TimeProvider>()))
            .AddSingleton<PrintQueueGate>()
            .AddSingleton(sp => new PrintQueueCoordinator(
                sp.GetRequiredService<IPrintSpool>(),
                sp.GetRequiredService<IPrinterClient>(),
                sp.GetRequiredService<PrintQueueGate>(),
                sp.GetRequiredService<TimeProvider>(),
                TimeSpan.FromMilliseconds(settings.SubmitDebounceMilliseconds),
                TimeSpan.FromMilliseconds(settings.ReconcileGraceMilliseconds)))
            .AddSingleton(sp => new PrinterQueueFileSystem(
                sp.GetRequiredService<IPrintSpool>(),
                sp.GetRequiredService<IPrinterClient>(),
                sp.GetRequiredService<PrintQueueGate>(),
                settings.SupportedFormats))
            .AddHostedService<PrintSubmissionWorker>();

        services
            .AddMcpServer()
            .WithHttpTransport()
            .AddFileSystemTools<PrinterQueueFileSystem>()
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