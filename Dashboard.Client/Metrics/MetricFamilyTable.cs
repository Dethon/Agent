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
            async () =>
            {
                var state = tokens.State;
                var breakdown = await api.GetGroupedAsync<decimal>(
                    $"tokens/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString())]);
                tokens.SetBreakdown(breakdown ?? []);
            },
            async () =>
            {
                var state = tokens.State;
                tokens.SetEvents(await api.GetTokensAsync(state.From, state.To) ?? []);
            });

        Tools = new MetricFamily<ToolsStore>(
            tools,
            "tools",
            async () =>
            {
                var state = tools.State;
                var breakdown = await api.GetGroupedAsync<decimal>(
                    $"tools/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString())]);
                tools.SetBreakdown(breakdown ?? []);
            },
            async () =>
            {
                var state = tools.State;
                tools.SetEvents(await api.GetToolsAsync(state.From, state.To) ?? []);
            });

        Errors = new MetricFamily<ErrorsStore>(
            errors,
            "errors",
            async () =>
            {
                var state = errors.State;
                var breakdown = await api.GetGroupedAsync<int>(
                    $"errors/by/{state.GroupBy}", state.From, state.To);
                errors.SetBreakdown(breakdown ?? []);
            },
            async () =>
            {
                var state = errors.State;
                errors.SetEvents(await api.GetErrorsAsync(state.From, state.To) ?? []);
            });

        Schedules = new MetricFamily<SchedulesStore>(
            schedules,
            "schedules",
            async () =>
            {
                var state = schedules.State;
                var breakdown = await api.GetGroupedAsync<int>(
                    $"schedules/by/{state.GroupBy}", state.From, state.To);
                schedules.SetBreakdown(breakdown ?? []);
            },
            async () =>
            {
                var state = schedules.State;
                schedules.SetEvents(await api.GetSchedulesAsync(state.From, state.To) ?? []);
            });

        Memory = new MetricFamily<MemoryStore>(
            memory,
            "memory",
            async () =>
            {
                var state = memory.State;
                var breakdown = await api.GetGroupedAsync<decimal>(
                    $"memory/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString())]);
                memory.SetBreakdown(breakdown ?? []);
            },
            async () =>
            {
                var state = memory.State;
                var recall = api.GetMemoryRecallAsync(state.From, state.To);
                var extraction = api.GetMemoryExtractionAsync(state.From, state.To);
                var dreaming = api.GetMemoryDreamingAsync(state.From, state.To);
                await Task.WhenAll(recall, extraction, dreaming);
                memory.SetRecallEvents(await recall ?? []);
                memory.SetExtractionEvents(await extraction ?? []);
                memory.SetDreamingEvents(await dreaming ?? []);
            });

        Latency = new MetricFamily<LatencyStore>(
            latency,
            "latency",
            async () =>
            {
                var state = latency.State;
                var breakdown = await api.GetGroupedAsync<decimal>(
                    $"latency/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString())]);
                var trend = await api.GetLatencyTrendAsync(state.Metric, state.From, state.To);
                latency.SetBreakdown(breakdown ?? []);
                latency.SetTrend(trend ?? []);
            },
            async () =>
            {
                var state = latency.State;
                latency.SetEvents(await api.GetLatencyAsync(state.From, state.To) ?? []);
            });

        Voice = new MetricFamily<VoiceStore>(
            voice,
            "voice",
            async () =>
            {
                var state = voice.State;
                var breakdown = await api.GetGroupedAsync<decimal>(
                    $"voice/by/{state.GroupBy}", state.From, state.To,
                    [("metric", state.Metric.ToString()), ("agg", state.Agg.ToString())]);
                voice.SetBreakdown(breakdown ?? []);
            },
            async () =>
            {
                var state = voice.State;
                voice.SetEvents(await api.GetVoiceEventsAsync(state.From, state.To) ?? []);
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