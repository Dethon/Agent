using Dashboard.Client.Services;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Latency;
using Dashboard.Client.State.Memory;
using Dashboard.Client.State.Schedules;
using Dashboard.Client.State.Tokens;
using Dashboard.Client.State.Tools;
using Dashboard.Client.State.Voice;

namespace Dashboard.Client.Metrics;

// The one place a metric family is declared. Nothing enforces that a new family is added here, so a
// missing one shows up as a gap in a seven-entry list rather than as a compiler error; that is the
// price recorded in docs/adr/0007-a-metric-family-is-named-not-typed.md.
public sealed class MetricFamilyTable
{
    public MetricFamilyTable(
        MetricsApiService api,
        TokensStore tokens,
        ToolsStore tools,
        ErrorsStore errors,
        SchedulesStore schedules,
        MemoryStore memory,
        LatencyStore latency,
        VoiceStore voice)
    {
        Tokens = new MetricFamily<TokensStore>(
            tokens,
            "tokens",
            setDateRange: tokens.SetDateRange,
            loadEvents: async () =>
            {
                var state = tokens.State;
                tokens.SetEvents(await api.GetTokensAsync(state.From, state.To) ?? []);
            },
            refreshBreakdown: async () =>
            {
                var state = tokens.State;
                var breakdown = await api.GetGroupedAsync<decimal>(
                    $"tokens/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString())]);
                tokens.SetBreakdown(breakdown ?? []);
            });

        Tools = new MetricFamily<ToolsStore>(
            tools,
            "tools",
            setDateRange: tools.SetDateRange,
            loadEvents: async () =>
            {
                var state = tools.State;
                tools.SetEvents(await api.GetToolsAsync(state.From, state.To) ?? []);
            },
            refreshBreakdown: async () =>
            {
                var state = tools.State;
                var breakdown = await api.GetGroupedAsync<decimal>(
                    $"tools/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString())]);
                tools.SetBreakdown(breakdown ?? []);
            });

        Errors = new MetricFamily<ErrorsStore>(
            errors,
            "errors",
            setDateRange: errors.SetDateRange,
            loadEvents: async () =>
            {
                var state = errors.State;
                errors.SetEvents(await api.GetErrorsAsync(state.From, state.To) ?? []);
            },
            refreshBreakdown: async () =>
            {
                var state = errors.State;
                var breakdown = await api.GetGroupedAsync<int>(
                    $"errors/by/{state.GroupBy}", state.From, state.To);
                errors.SetBreakdown(breakdown ?? []);
            });

        Schedules = new MetricFamily<SchedulesStore>(
            schedules,
            "schedules",
            setDateRange: schedules.SetDateRange,
            loadEvents: async () =>
            {
                var state = schedules.State;
                schedules.SetEvents(await api.GetSchedulesAsync(state.From, state.To) ?? []);
            },
            refreshBreakdown: async () =>
            {
                var state = schedules.State;
                var breakdown = await api.GetGroupedAsync<int>(
                    $"schedules/by/{state.GroupBy}", state.From, state.To);
                schedules.SetBreakdown(breakdown ?? []);
            });

        Memory = new MetricFamily<MemoryStore>(
            memory,
            "memory",
            setDateRange: memory.SetDateRange,
            loadEvents: async () =>
            {
                var state = memory.State;
                var recall = api.GetMemoryRecallAsync(state.From, state.To);
                var extraction = api.GetMemoryExtractionAsync(state.From, state.To);
                var dreaming = api.GetMemoryDreamingAsync(state.From, state.To);
                await Task.WhenAll(recall, extraction, dreaming);
                memory.SetRecallEvents(await recall ?? []);
                memory.SetExtractionEvents(await extraction ?? []);
                memory.SetDreamingEvents(await dreaming ?? []);
            },
            refreshBreakdown: async () =>
            {
                var state = memory.State;
                var breakdown = await api.GetGroupedAsync<decimal>(
                    $"memory/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString())]);
                memory.SetBreakdown(breakdown ?? []);
            });

        Latency = new MetricFamily<LatencyStore>(
            latency,
            "latency",
            setDateRange: latency.SetDateRange,
            loadEvents: async () =>
            {
                var state = latency.State;
                latency.SetEvents(await api.GetLatencyAsync(state.From, state.To) ?? []);
            },
            // The trend is the second panel on the latency page. It is fetched alongside the
            // breakdown and written with it, so the two panels can never disagree.
            refreshBreakdown: async () =>
            {
                var state = latency.State;
                var breakdown = api.GetGroupedAsync<decimal>(
                    $"latency/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString())]);
                var trend = api.GetLatencyTrendAsync(state.Metric, state.From, state.To);
                await Task.WhenAll(breakdown, trend);
                latency.SetBreakdown(await breakdown ?? []);
                latency.SetTrend(await trend ?? []);
            });

        Voice = new MetricFamily<VoiceStore>(
            voice,
            "voice",
            setDateRange: voice.SetDateRange,
            loadEvents: async () =>
            {
                var state = voice.State;
                voice.SetEvents(await api.GetVoiceEventsAsync(state.From, state.To) ?? []);
            },
            refreshBreakdown: async () =>
            {
                var state = voice.State;
                var breakdown = await api.GetGroupedAsync<decimal>(
                    $"voice/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString()), ("agg", state.Agg.ToString())]);
                voice.SetBreakdown(breakdown ?? []);
            });

        All = [Tokens, Tools, Errors, Schedules, Memory, Latency, Voice];
    }

    public MetricFamily<TokensStore> Tokens { get; }
    public MetricFamily<ToolsStore> Tools { get; }
    public MetricFamily<ErrorsStore> Errors { get; }
    public MetricFamily<SchedulesStore> Schedules { get; }
    public MetricFamily<MemoryStore> Memory { get; }
    public MetricFamily<LatencyStore> Latency { get; }
    public MetricFamily<VoiceStore> Voice { get; }

    public IReadOnlyList<MetricFamily> All { get; }
}