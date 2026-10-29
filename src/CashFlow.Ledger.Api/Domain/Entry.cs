using CashFlow.Shared.Results;

namespace CashFlow.Ledger.Api.Domain;

/// <summary>
/// Agregado raiz do Ledger. Lançamento imutável (RN-01, append-only):
/// positivo = crédito, negativo = débito (RN-03 — não existe enum Debit/Credit).
/// Estorno é um novo <see cref="Entry"/> de sinal oposto com
/// <see cref="ReversesEntryId"/> apontando para o original.
/// </summary>
public sealed class Entry
{
    public const int MaxDescriptionLength = 280;

    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public decimal Amount { get; private set; }
    public DateOnly OccurredOn { get; private set; }
    public string Description { get; private set; } = default!;
    public string IdempotencyKey { get; private set; } = default!;
    public Guid? ReversesEntryId { get; private set; }
    public string CorrelationId { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }

    private Entry() { } // EF Core

    public static Result<Entry> Create(
        Guid merchantId,
        decimal amount,
        DateOnly occurredOn,
        string? description,
        string? idempotencyKey,
        string correlationId,
        DateOnly today,
        Guid? reversesEntryId = null)
    {
        var errors = new Dictionary<string, List<string>>();

        if (merchantId == Guid.Empty)
            errors["merchantId"] = new() { "O comerciante é obrigatório." };

        // RN-04: MidpointRounding.ToEven evita viés sistemático de centavos.
        var rounded = Math.Round(amount, 2, MidpointRounding.ToEven);
        if (rounded == 0m)
            errors["entry"] = new() { "O valor do lançamento não pode ser zero." };

        if (occurredOn == default)
            errors["occurredOn"] = new() { "A data de competência é obrigatória." };
        else if (occurredOn > today)
            errors["occurredOn"] = new() { "A data de competência não pode ser futura." };

        if (string.IsNullOrWhiteSpace(description))
            errors["description"] = new() { "A descrição é obrigatória." };
        else if (description.Trim().Length > MaxDescriptionLength)
            errors["description"] = new() { $"A descrição deve ter no máximo {MaxDescriptionLength} caracteres." };

        if (string.IsNullOrWhiteSpace(idempotencyKey))
            errors["idempotencyKey"] = new() { "A chave de idempotência é obrigatória." };

        if (errors.Count > 0)
            return Result<Entry>.Failure(errors);

        return Result<Entry>.Success(new Entry
        {
            Id = Guid.NewGuid(),
            MerchantId = merchantId,
            Amount = rounded,
            OccurredOn = occurredOn,
            Description = description!.Trim(),
            IdempotencyKey = idempotencyKey!,
            ReversesEntryId = reversesEntryId,
            CorrelationId = correlationId,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// RN-01: estorno é um novo lançamento de sinal oposto na mesma data de
    /// competência (RN-02), de modo que o estorno zere o original no próprio dia.
    /// O original permanece intacto.
    /// </summary>
    public Result<Entry> CreateReversal(string idempotencyKey, string correlationId, DateOnly today)
    {
        var description = $"Estorno: {Description}";
        if (description.Length > MaxDescriptionLength)
            description = description[..MaxDescriptionLength];

        return Create(MerchantId, -Amount, OccurredOn, description, idempotencyKey, correlationId, today, Id);
    }
}
