using CashFlow.Ledger.Api.Domain;
using FluentAssertions;

namespace CashFlow.Ledger.Tests;

public class EntryTests
{
    private static readonly Guid MerchantId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 29);
    private const string CorrelationId = "test-correlation";

    [Fact]
    public void Create_ComDadosValidos_CriaLancamento()
    {
        var result = Entry.Create(MerchantId, 150.00m, Today, "Venda de balcão", "key-1", CorrelationId, Today);

        result.IsSuccess.Should().BeTrue();
        var entry = result.Value!;
        entry.Id.Should().NotBeEmpty();
        entry.MerchantId.Should().Be(MerchantId);
        entry.Amount.Should().Be(150.00m);
        entry.OccurredOn.Should().Be(Today);
        entry.Description.Should().Be("Venda de balcão");
        entry.IdempotencyKey.Should().Be("key-1");
        entry.ReversesEntryId.Should().BeNull();
        entry.CorrelationId.Should().Be(CorrelationId);
        entry.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Create_ComValorZero_EhRejeitado()
    {
        var result = Entry.Create(MerchantId, 0m, Today, "Venda", "key-1", CorrelationId, Today);

        result.IsFailure.Should().BeTrue();
        result.Errors["entry"].Should().Contain("O valor do lançamento não pode ser zero.");
    }

    [Fact]
    public void Create_ComValorQueArredondaParaZero_EhRejeitado()
    {
        var result = Entry.Create(MerchantId, 0.001m, Today, "Venda", "key-1", CorrelationId, Today);

        result.IsFailure.Should().BeTrue();
        result.Errors["entry"].Should().Contain("O valor do lançamento não pode ser zero.");
    }

    [Fact]
    public void Create_ComMerchantIdVazio_EhRejeitado()
    {
        var result = Entry.Create(Guid.Empty, 100m, Today, "Venda", "key-1", CorrelationId, Today);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainKey("merchantId");
    }

    [Fact]
    public void Create_ComDataFutura_EhRejeitado()
    {
        var result = Entry.Create(MerchantId, 100m, Today.AddDays(1), "Venda", "key-1", CorrelationId, Today);

        result.IsFailure.Should().BeTrue();
        result.Errors["occurredOn"].Should().Contain("A data de competência não pode ser futura.");
    }

    [Fact]
    public void Create_SemDescricao_EhRejeitado()
    {
        var result = Entry.Create(MerchantId, 100m, Today, " ", "key-1", CorrelationId, Today);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainKey("description");
    }

    [Fact]
    public void Create_ComDescricaoAcimaDe280_EhRejeitado()
    {
        var result = Entry.Create(MerchantId, 100m, Today, new string('x', 281), "key-1", CorrelationId, Today);

        result.IsFailure.Should().BeTrue();
        result.Errors["description"].Should().Contain(m => m.Contains("280"));
    }

    [Fact]
    public void Create_SemIdempotencyKey_EhRejeitado()
    {
        var result = Entry.Create(MerchantId, 100m, Today, "Venda", null, CorrelationId, Today);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainKey("idempotencyKey");
    }

    // RN-04: MidpointRounding.ToEven — sem viés sistemático de centavos.
    [Theory]
    [InlineData(10.005, 10.00)]
    [InlineData(10.015, 10.02)]
    [InlineData(2.675, 2.68)]
    [InlineData(-10.005, -10.00)]
    [InlineData(0.004, 0.00)]
    public void Create_ArredondaComToEven(decimal input, decimal expected)
    {
        var result = Entry.Create(MerchantId, input, Today, "Venda", "key-1", CorrelationId, Today);

        if (expected == 0m)
            result.IsFailure.Should().BeTrue();
        else
            result.Value!.Amount.Should().Be(expected);
    }

    [Fact]
    public void CreateReversal_CriaLancamentoDeSinalOposto()
    {
        var original = Entry.Create(MerchantId, 150.00m, Today, "Venda", "key-1", CorrelationId, Today).Value!;

        var reversal = original.CreateReversal("reverse-1", CorrelationId, Today);

        reversal.IsSuccess.Should().BeTrue();
        reversal.Value!.Amount.Should().Be(-150.00m);
        reversal.Value!.ReversesEntryId.Should().Be(original.Id);
        reversal.Value!.OccurredOn.Should().Be(original.OccurredOn);
        reversal.Value!.MerchantId.Should().Be(original.MerchantId);
        reversal.Value!.Description.Should().Be("Estorno: Venda");
    }

    [Fact]
    public void CreateReversal_NaoAlteraLancamentoOriginal()
    {
        var original = Entry.Create(MerchantId, 150.00m, Today, "Venda", "key-1", CorrelationId, Today).Value!;

        original.CreateReversal("reverse-1", CorrelationId, Today);

        original.Amount.Should().Be(150.00m);
        original.ReversesEntryId.Should().BeNull();
    }
}
