namespace Domain.DTOs;

public abstract record ParseResult;

public sealed record ParseSuccess(ParsedServiceBusMessage Message) : ParseResult;

public sealed record ParseFailure(string Reason, string Details) : ParseResult;
