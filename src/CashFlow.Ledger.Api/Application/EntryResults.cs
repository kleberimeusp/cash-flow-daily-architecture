using CashFlow.Ledger.Api.Domain;

namespace CashFlow.Ledger.Api.Application;

public abstract record CreateEntryResult
{
    public sealed record Created(Entry Entry) : CreateEntryResult;
    public sealed record Duplicate(Entry Entry) : CreateEntryResult;
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateEntryResult;
}

public abstract record ReverseEntryResult
{
    public sealed record Created(Entry Entry) : ReverseEntryResult;
    public sealed record Duplicate(Entry Entry) : ReverseEntryResult;
    public sealed record NotFound : ReverseEntryResult;
    public sealed record Conflict(string Message) : ReverseEntryResult;
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : ReverseEntryResult;
}
