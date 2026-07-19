namespace Domain.DTOs.Voice;

public record InsistentOptions
{
    public int? GapSeconds { get; init; }
    public int? MaxRepeats { get; init; }
    public int? MaxDurationSeconds { get; init; }
    // Round-1 gain in percent (ramping to 100); 100 disables the ramp. Timers use this to ring at
    // full volume from the first round while alarms keep the configured wake-up ramp.
    public int? RampStartPercent { get; init; }
}