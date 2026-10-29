namespace CashFlow.Shared.Results;

/// <summary>
/// Resultado de operação com erros por campo, serializado como
/// <c>{"errors": {"campo": ["mensagem"]}}</c> nas respostas 400.
/// </summary>
public class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    protected Result(bool isSuccess, IReadOnlyDictionary<string, string[]>? errors)
    {
        IsSuccess = isSuccess;
        Errors = errors ?? new Dictionary<string, string[]>();
    }

    public static Result Success() => new(true, null);

    public static Result Failure(string field, string message)
        => new(false, new Dictionary<string, string[]> { [field] = new[] { message } });

    public static Result Failure(IDictionary<string, List<string>> errors)
        => new(false, errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
}

public sealed class Result<T> : Result
{
    public T? Value { get; }

    private Result(bool isSuccess, T? value, IReadOnlyDictionary<string, string[]>? errors)
        : base(isSuccess, errors) => Value = value;

    public static Result<T> Success(T value) => new(true, value, null);

    public new static Result<T> Failure(string field, string message)
        => new(false, default, new Dictionary<string, string[]> { [field] = new[] { message } });

    public new static Result<T> Failure(IDictionary<string, List<string>> errors)
        => new(false, default, errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
}
