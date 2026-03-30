using System.Net.Http.Headers;
using Domain.Contracts;
using Domain.Memory;
using Domain.Tools.Memory;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Memory;
using OpenAI;
using System.ClientModel;
using Microsoft.Extensions.AI;

namespace Agent.Modules;

public static class MemoryModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddMemory(IConfiguration config)
        {
            var memoryConfig = config.GetSection("Memory");

            // Extraction queue
            services.AddSingleton<MemoryExtractionQueue>();

            // Infrastructure — store and embeddings
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
            });

            // LLM-based services — extractor and consolidator
            services.AddSingleton<IMemoryExtractor>(sp =>
            {
                var openRouterConfig = config.GetSection("openRouter");
                var extractionModel = memoryConfig["Extraction:Model"] ?? "google/gemini-2.0-flash-001";
                var metricsPublisher = sp.GetRequiredService<IMetricsPublisher>();
                var chatClient = new OpenRouterChatClient(
                    openRouterConfig["apiUrl"]!, openRouterConfig["apiKey"]!,
                    extractionModel, metricsPublisher);
                return new OpenRouterMemoryExtractor(
                    chatClient,
                    sp.GetRequiredService<IMemoryStore>(),
                    sp.GetRequiredService<ILogger<OpenRouterMemoryExtractor>>());
            });

            services.AddSingleton<IMemoryConsolidator>(sp =>
            {
                var openRouterConfig = config.GetSection("openRouter");
                var dreamingModel = memoryConfig["Dreaming:Model"] ?? "google/gemini-2.0-flash-001";
                var metricsPublisher = sp.GetRequiredService<IMetricsPublisher>();
                var chatClient = new OpenRouterChatClient(
                    openRouterConfig["apiUrl"]!, openRouterConfig["apiKey"]!,
                    dreamingModel, metricsPublisher);
                return new OpenRouterMemoryConsolidator(
                    chatClient,
                    sp.GetRequiredService<ILogger<OpenRouterMemoryConsolidator>>());
            });

            // Options
            var recallOptions = new MemoryRecallOptions
            {
                DefaultLimit = memoryConfig.GetValue("Recall:DefaultLimit", 10),
                IncludePersonalityProfile = memoryConfig.GetValue("Recall:IncludePersonalityProfile", true)
            };
            services.AddSingleton(recallOptions);

            var extractionOptions = new MemoryExtractionOptions
            {
                SimilarityThreshold = memoryConfig.GetValue("Extraction:SimilarityThreshold", 0.85),
                MaxCandidatesPerMessage = memoryConfig.GetValue("Extraction:MaxCandidatesPerMessage", 5)
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

            // Hook
            services.AddSingleton<IMemoryRecallHook, MemoryRecallHook>();

            // Domain tool feature
            services.AddTransient<IDomainToolFeature, MemoryToolFeature>();

            // Background workers
            services.AddHostedService<MemoryExtractionWorker>();
            services.AddHostedService<MemoryDreamingService>();

            return services;
        }
    }
}
