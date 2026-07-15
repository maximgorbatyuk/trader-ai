using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class AiDecisionJsonTests
{
    private const int MaxOrders = 10;

    private const string ValidJson = """
    {
      "summary": "Reduce exposure to a falling company and buy a stronger one.",
      "cancelOrderIds": [11, 12],
      "orders": [
        { "side": "Sell", "companyId": 17, "quantity": 25, "limitPrice": 94.50, "reason": "Declining capitalization." },
        { "side": "Buy", "companyId": 42, "quantity": 10, "limitPrice": 121.25, "reason": "Positive trend." }
      ]
    }
    """;

    [Fact]
    public void ValidJsonParses()
    {
        Assert.True(AiDecisionJson.TryParse(ValidJson, MaxOrders, out var decision, out var error));
        Assert.Null(error);
        Assert.NotNull(decision);
        Assert.Equal(2, decision!.Orders.Length);
        Assert.Equal([11, 12], decision.CancelOrderIds);
        Assert.Equal(OrderType.Sell, decision.Orders[0].Side);
        Assert.Equal(OrderType.Buy, decision.Orders[1].Side);
    }

    [Fact]
    public void EmptyOrdersIsAValidWaitDecision()
    {
        var json = """{ "summary": "Wait this cycle.", "cancelOrderIds": [], "orders": [] }""";
        Assert.True(AiDecisionJson.TryParse(json, MaxOrders, out var decision, out _));
        Assert.Empty(decision!.Orders);
    }

    [Fact]
    public void MissingCancelOrderIdsFailsForFreshAssistantContent()
    {
        var json = """{ "summary": "Wait this cycle.", "orders": [] }""";

        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out var error));
        Assert.Contains("cancelOrderIds", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCancelOrderIdFails(int orderId)
    {
        var json = $$"""{ "summary": "Cancel stale interest.", "cancelOrderIds": [{{orderId}}], "orders": [] }""";

        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out var error));
        Assert.Contains("cancelOrderIds", error);
    }

    [Fact]
    public void DuplicateCancelOrderIdsFail()
    {
        var json = """{ "summary": "Cancel stale orders.", "cancelOrderIds": [7, 7], "orders": [] }""";

        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out var error));
        Assert.Contains("unique", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MoreThanMaxCancelOrderIdsFail()
    {
        var cancelOrderIds = string.Join(",", Enumerable.Range(1, MaxOrders + 1));
        var json = $$"""{ "summary": "Cancel stale orders.", "cancelOrderIds": [{{cancelOrderIds}}], "orders": [] }""";

        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out var error));
        Assert.Contains($"at most {MaxOrders}", error);
    }

    [Fact]
    public void MarkdownFenceFails()
    {
        var json = "```json\n" + ValidJson + "\n```";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void SurroundingProseFails()
    {
        var json = "Here is my decision: " + ValidJson;
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void MissingSummaryFails()
    {
        var json = """{ "cancelOrderIds": [], "orders": [] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void BlankSummaryFails()
    {
        var json = """{ "summary": "   ", "cancelOrderIds": [], "orders": [] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void UnknownPropertyFails()
    {
        var json = """{ "summary": "x", "cancelOrderIds": [], "orders": [], "extra": true }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void UnknownOrderPropertyFails()
    {
        var json = """
        { "summary": "x", "cancelOrderIds": [], "orders": [ { "side": "Buy", "companyId": 1, "quantity": 1, "limitPrice": 1, "reason": "r", "note": "n" } ] }
        """;
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void InvalidSideFails()
    {
        var json = """{ "summary": "x", "cancelOrderIds": [], "orders": [ { "side": "Hold", "companyId": 1, "quantity": 1, "limitPrice": 1, "reason": "r" } ] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void IntegerSideValueFails()
    {
        var json = """{ "summary": "x", "cancelOrderIds": [], "orders": [ { "side": 0, "companyId": 1, "quantity": 1, "limitPrice": 1, "reason": "r" } ] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveQuantityFails(int quantity)
    {
        var json = $$"""{ "summary": "x", "cancelOrderIds": [], "orders": [ { "side": "Buy", "companyId": 1, "quantity": {{quantity}}, "limitPrice": 1, "reason": "r" } ] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void NonPositivePriceFails()
    {
        var json = """{ "summary": "x", "cancelOrderIds": [], "orders": [ { "side": "Buy", "companyId": 1, "quantity": 1, "limitPrice": 0, "reason": "r" } ] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void NonPositiveCompanyIdFails()
    {
        var json = """{ "summary": "x", "cancelOrderIds": [], "orders": [ { "side": "Buy", "companyId": 0, "quantity": 1, "limitPrice": 1, "reason": "r" } ] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void BlankReasonFails()
    {
        var json = """{ "summary": "x", "cancelOrderIds": [], "orders": [ { "side": "Buy", "companyId": 1, "quantity": 1, "limitPrice": 1, "reason": "  " } ] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void MoreThanMaxOrdersFails()
    {
        var orders = string.Join(",", Enumerable.Range(1, MaxOrders + 1).Select(id =>
            $$"""{ "side": "Buy", "companyId": {{id}}, "quantity": 1, "limitPrice": 1, "reason": "r" }"""));
        var json = $$"""{ "summary": "x", "cancelOrderIds": [], "orders": [ {{orders}} ] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }
}
