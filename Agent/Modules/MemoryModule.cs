using System.ClientModel;
using System.Net.Http.Headers;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Memory;
using Domain.Tools.Memory;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Memory;
using Infrastructure.Validation;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Agent.Modules;

public static class MemoryModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddMemory(IConfiguration config)
        {
            var memoryConfig = config.GetSection("Memory");

            services.AddSingleton<MemoryExtractionQueue>();

            services.AddSingleton<IMemoryStore, RedisStackMemoryStore>();
            services.AddHttpClient<IEmbeddingService, OpenRouterEmbeddingService>((httpClient, sp) =>
            {
                var openRouterConfig = config.GetSection("openRouter");
                httpClient.BaseAddress = new Uri(openRouterConfig["apiUrl"]!);
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", openRouterConfig["apiKey"]);
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                var embeddingModel = memoryConfig["Embedding:Model"] ?? "openai/text-embedding-3-small";
                return new OpenRouterEmbeddingService(httpClient, embeddingModel);
            })
                .ConfigurePrimaryHttpMessageHandler(HostedConnectionPool.CreateHandler)
                // The factory's own two-minute handler rotation would throw the pool away well
                // inside the connection lifetime the handler is configured for, so the handler
                // outlives the factory's default and manages pooling itself.
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

            services.AddSingleton<IMemoryExtractor>(sp =>
            {
                var openRouterConfig = config.GetSection("openRouter");
                var providerRouting = openRouterConfig.GetSection("providerRouting").Get<ProviderRouting>();
                var extractionModel = memoryConfig["Extraction:Model"] ?? "z-ai/glm-4.7-flash";
                var metricsPublisher = sp.GetRequiredService<IMetricsPublisher>();
                var chatClient = new OpenRouterChatClient(
                    openRouterConfig["apiUrl"]!, openRouterConfig["apiKey"]!,
                    extractionModel,
                    maxContextTokens: openRouterConfig.GetValue<int?>("maxContextTokens"),
                    metricsPublisher: metricsPublisher,
                    providerRouting: providerRouting);
                return new OpenRouterMemoryExtractor(
                    chatClient,
                    sp.GetRequiredService<IMemoryStore>(),
                    sp.GetRequiredService<ILogger<OpenRouterMemoryExtractor>>());
            });

            services.AddSingleton<IMemoryConsolidator>(sp =>
            {
                var openRouterConfig = config.GetSection("openRouter");
                var providerRouting = openRouterConfig.GetSection("providerRouting").Get<ProviderRouting>();
                var dreamingModel = memoryConfig["Dreaming:Model"] ?? "z-ai/glm-4.7-flash";
                var metricsPublisher = sp.GetRequiredService<IMetricsPublisher>();
                var chatClient = new OpenRouterChatClient(
                    openRouterConfig["apiUrl"]!, openRouterConfig["apiKey"]!,
                    dreamingModel,
                    maxContextTokens: openRouterConfig.GetValue<int?>("maxContextTokens"),
                    metricsPublisher: metricsPublisher,
                    providerRouting: providerRouting);
                return new OpenRouterMemoryConsolidator(
                    chatClient,
                    sp.GetRequiredService<ILogger<OpenRouterMemoryConsolidator>>());
            });

            var recallOptions = new MemoryRecallOptions
            {
                DefaultLimit = memoryConfig.GetValue("Recall:DefaultLimit", 10),
                IncludePersonalityProfile = memoryConfig.GetValue("Recall:IncludePersonalityProfile", true),
                WindowUserTurns = memoryConfig.GetValue("Recall:WindowUserTurns", 3)
            };
            services.AddSingleton(recallOptions);

            var extractionOptions = new MemoryExtractionOptions
            {
                SimilarityThreshold = memoryConfig.GetValue("Extraction:SimilarityThreshold", 0.85),
                MaxCandidatesPerMessage = memoryConfig.GetValue("Extraction:MaxCandidatesPerMessage", 5),
                WindowMixedTurns = memoryConfig.GetValue("Extraction:WindowMixedTurns", 6)
            };
            services.AddSingleton(extractionOptions);

            var dreamingOptions = new MemoryDreamingOptions
            {
                CronSchedule = memoryConfig["Dreaming:CronSchedule"] ?? "0 3 * * *",
                DecayDays = memoryConfig.GetValue("Dreaming:DecayDays", 30),
                DecayFactor = memoryConfig.GetValue("Dreaming:DecayFactor", 0.9),
                DecayFloor = memoryConfig.GetValue("Dreaming:DecayFloor", 0.1)
            };
            services.AddSingleton(dreamingOptions);

            services.AddSingleton<IMemoryRecallHook, MemoryRecallHook>();

            services.AddTransient<IDomainToolFeature, MemoryToolFeature>();

            services.AddSingleton<ICronValidator, CronValidator>();
            services.AddHostedService<MemoryExtractionWorker>();
            services.AddHostedService<MemoryDreamingService>();

            return services;
        }
    }
}