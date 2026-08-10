namespace VlmHub.Balancer.Models;

public sealed record Status(
    string State,
    string? ActiveModel,
    string? LastError,
    DateTimeOffset? LastStateChange);
