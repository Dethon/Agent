using System.Reflection;
using Domain.DTOs.Metrics.Enums;
using Observability.Services;
using Shouldly;

namespace Tests.Unit.Observability.Services;

public class VoiceMetricsQueryTests
{
    [Fact]
    public void MetricsQueryService_HasGetVoiceGroupedAsync()
    {
        var method = typeof(MetricsQueryService).GetMethod(
            "GetVoiceGroupedAsync",
            BindingFlags.Public | BindingFlags.Instance);
        method.ShouldNotBeNull();

        var parameters = method!.GetParameters().Select(p => p.ParameterType).ToArray();
        parameters.ShouldContain(typeof(VoiceDimension));
        parameters.ShouldContain(typeof(VoiceMetric));
    }
}