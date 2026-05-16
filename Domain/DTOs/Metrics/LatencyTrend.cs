namespace Domain.DTOs.Metrics;

public record LatencyTrendPoint(DateTimeOffset Bucket, decimal Value);

public record LatencyTrendSeries(string Stage, IReadOnlyList<LatencyTrendPoint> Points);