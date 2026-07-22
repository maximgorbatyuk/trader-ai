using TraderAi.Models;
using TraderAi.Services;

namespace TraderAi.Tests;

public sealed class AiDecisionJsonTests
{
    private const int MaxOrders = 10;
    private const int MaxPredictions = 2;
    private const int PredictionHorizon = 210;

    private const string ValidJson = """
    {
      "summary": "Reduce exposure to a falling company and buy a stronger one.",
      "cancelOrderIds": [11, 12],
      "bigInvestment": null,
      "orders": [
        { "side": "Sell", "companyId": 17, "quantity": 25, "limitPrice": 94.50, "reason": "Declining capitalization." },
        { "side": "Buy", "companyId": 42, "quantity": 10, "limitPrice": 121.25, "reason": "Positive trend." }
      ]
    }
    """;

    [Fact]
    public void ValidPredictionParses()
    {
        var json = """
        {
          "summary": "Expect continued strength.",
          "cancelOrderIds": [],
          "bigInvestment": null,
          "orders": [],
          "predictions": [
            { "companyId": 42, "direction": "Up", "confidence": 0.72, "horizonCycles": 210, "targetPrice": 125.50, "reason": "Positive demand and cash flow." }
          ]
        }
        """;

        Assert.True(AiDecisionJson.TryParse(
            json, MaxOrders, MaxPredictions, PredictionHorizon, out var decision, out var error));
        Assert.Null(error);
        var prediction = Assert.Single(decision!.Predictions);
        Assert.Equal(AiPredictionDirection.Up, prediction.Direction);
        Assert.Equal(0.72m, prediction.Confidence);
        Assert.Equal(210, prediction.HorizonCycles);
        Assert.Equal(125.50m, prediction.TargetPrice);
    }

    [Fact]
    public void ExplicitEmptyPredictionsArrayParses()
    {
        var json = """{ "summary": "No forecast.", "cancelOrderIds": [], "bigInvestment": null, "orders": [], "predictions": [] }""";

        Assert.True(AiDecisionJson.TryParse(
            json, MaxOrders, MaxPredictions, PredictionHorizon, out var decision, out var error));
        Assert.Null(error);
        Assert.Empty(decision!.Predictions);
    }

    [Fact]
    public void MissingPredictionsFails()
    {
        var json = """{ "summary": "No forecast.", "cancelOrderIds": [], "bigInvestment": null, "orders": [] }""";

        Assert.False(AiDecisionJson.TryParse(
            json, MaxOrders, MaxPredictions, PredictionHorizon, out _, out var error));
        Assert.Contains("predictions", error);
    }

    [Theory]
    [InlineData("Sideways")]
    [InlineData("up")]
    public void UnknownPredictionDirectionFails(string direction)
    {
        var json = PredictionJson(direction: direction);
        Assert.False(AiDecisionJson.TryParse(
            json, MaxOrders, MaxPredictions, PredictionHorizon, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("0.49")]
    [InlineData("1.01")]
    public void PredictionConfidenceOutsideRangeFails(string confidence)
    {
        var json = PredictionJson(confidence: confidence);
        Assert.False(AiDecisionJson.TryParse(
            json, MaxOrders, MaxPredictions, PredictionHorizon, out _, out var error));
        Assert.Contains("confidence", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(209)]
    public void InvalidPredictionHorizonFails(int horizon)
    {
        var json = PredictionJson(horizon: horizon);
        Assert.False(AiDecisionJson.TryParse(
            json, MaxOrders, MaxPredictions, PredictionHorizon, out _, out var error));
        Assert.Contains("horizon", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonPositivePredictionTargetPriceFails()
    {
        var json = PredictionJson(targetPrice: "0");
        Assert.False(AiDecisionJson.TryParse(
            json, MaxOrders, MaxPredictions, PredictionHorizon, out _, out var error));
        Assert.Contains("targetPrice", error);
    }

    [Fact]
    public void DuplicateCompanyAndHorizonPredictionFails()
    {
        var prediction = """{ "companyId": 42, "direction": "Up", "confidence": 0.72, "horizonCycles": 210, "targetPrice": 125.50, "reason": "Demand." }""";
        var json = $$"""{ "summary": "Forecast.", "cancelOrderIds": [], "bigInvestment": null, "orders": [], "predictions": [{{prediction}}, {{prediction}}] }""";

        Assert.False(AiDecisionJson.TryParse(
            json, MaxOrders, MaxPredictions, PredictionHorizon, out _, out var error));
        Assert.Contains("unique", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PredictionCountAboveMaximumFails()
    {
        var predictions = string.Join(",", Enumerable.Range(1, MaxPredictions + 1).Select(companyId =>
            $$"""{ "companyId": {{companyId}}, "direction": "Up", "confidence": 0.72, "horizonCycles": 210, "targetPrice": null, "reason": "Demand." }"""));
        var json = $$"""{ "summary": "Forecast.", "cancelOrderIds": [], "bigInvestment": null, "orders": [], "predictions": [{{predictions}}] }""";

        Assert.False(AiDecisionJson.TryParse(
            json, MaxOrders, MaxPredictions, PredictionHorizon, out _, out var error));
        Assert.Contains($"at most {MaxPredictions}", error);
    }

    private static string PredictionJson(
        string direction = "Up",
        string confidence = "0.72",
        int horizon = PredictionHorizon,
        string targetPrice = "125.50")
        => $$"""{ "summary": "Forecast.", "cancelOrderIds": [], "bigInvestment": null, "orders": [], "predictions": [{ "companyId": 42, "direction": "{{direction}}", "confidence": {{confidence}}, "horizonCycles": {{horizon}}, "targetPrice": {{targetPrice}}, "reason": "Demand." }] }""";

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
    public void ArraySummaryIsJoinedIntoOneString()
    {
        var json = """
        {
          "summary": ["Exposure far below minimum", "Deploying large positions", "Avoiding paused companies"],
          "cancelOrderIds": [],
          "bigInvestment": null,
          "orders": []
        }
        """;

        Assert.True(AiDecisionJson.TryParse(json, MaxOrders, out var decision, out var error));
        Assert.Null(error);
        Assert.Equal("Exposure far below minimum; Deploying large positions; Avoiding paused companies", decision!.Summary);
    }

    [Fact]
    public void NonStringSummaryArrayElementFails()
    {
        var json = """{ "summary": ["ok", 3], "cancelOrderIds": [], "bigInvestment": null, "orders": [] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void EmptyOrdersIsAValidWaitDecision()
    {
        var json = """{ "summary": "Wait this cycle.", "cancelOrderIds": [], "bigInvestment": null, "orders": [] }""";
        Assert.True(AiDecisionJson.TryParse(json, MaxOrders, out var decision, out _));
        Assert.Empty(decision!.Orders);
    }

    [Fact]
    public void ValidBigInvestmentParses()
    {
        var json = """
        {
          "summary": "Fund the strongest company directly.",
          "cancelOrderIds": [],
          "bigInvestment": { "companyId": 42, "amount": 50000, "reason": "Strong long-term outlook." },
          "orders": []
        }
        """;

        Assert.True(AiDecisionJson.TryParse(json, MaxOrders, out var decision, out var error));
        Assert.Null(error);
        Assert.Equal(42, decision!.BigInvestment!.CompanyId);
        Assert.Equal(50_000m, decision.BigInvestment.Amount);
    }

    [Fact]
    public void MissingBigInvestmentFailsForFreshAssistantContent()
    {
        var json = """{ "summary": "Wait this cycle.", "cancelOrderIds": [], "orders": [] }""";

        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out var error));
        Assert.Contains("bigInvestment", error);
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
        var json = $$"""{ "summary": "Cancel stale interest.", "cancelOrderIds": [{{orderId}}], "bigInvestment": null, "orders": [] }""";

        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out var error));
        Assert.Contains("cancelOrderIds", error);
    }

    [Fact]
    public void DuplicateCancelOrderIdsFail()
    {
        var json = """{ "summary": "Cancel stale orders.", "cancelOrderIds": [7, 7], "bigInvestment": null, "orders": [] }""";

        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out var error));
        Assert.Contains("unique", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MoreThanMaxCancelOrderIdsFail()
    {
        var cancelOrderIds = string.Join(",", Enumerable.Range(1, MaxOrders + 1));
        var json = $$"""{ "summary": "Cancel stale orders.", "cancelOrderIds": [{{cancelOrderIds}}], "bigInvestment": null, "orders": [] }""";

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
        var json = """{ "cancelOrderIds": [], "bigInvestment": null, "orders": [] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void BlankSummaryFails()
    {
        var json = """{ "summary": "   ", "cancelOrderIds": [], "bigInvestment": null, "orders": [] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void UnknownPropertyFails()
    {
        var json = """{ "summary": "x", "cancelOrderIds": [], "bigInvestment": null, "orders": [], "extra": true }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void UnknownOrderPropertyFails()
    {
        var json = """
        { "summary": "x", "cancelOrderIds": [], "bigInvestment": null, "orders": [ { "side": "Buy", "companyId": 1, "quantity": 1, "limitPrice": 1, "reason": "r", "note": "n" } ] }
        """;
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void InvalidSideFails()
    {
        var json = """{ "summary": "x", "cancelOrderIds": [], "bigInvestment": null, "orders": [ { "side": "Hold", "companyId": 1, "quantity": 1, "limitPrice": 1, "reason": "r" } ] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void IntegerSideValueFails()
    {
        var json = """{ "summary": "x", "cancelOrderIds": [], "bigInvestment": null, "orders": [ { "side": 0, "companyId": 1, "quantity": 1, "limitPrice": 1, "reason": "r" } ] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveQuantityFails(int quantity)
    {
        var json = $$"""{ "summary": "x", "cancelOrderIds": [], "bigInvestment": null, "orders": [ { "side": "Buy", "companyId": 1, "quantity": {{quantity}}, "limitPrice": 1, "reason": "r" } ] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void NonPositivePriceFails()
    {
        var json = """{ "summary": "x", "cancelOrderIds": [], "bigInvestment": null, "orders": [ { "side": "Buy", "companyId": 1, "quantity": 1, "limitPrice": 0, "reason": "r" } ] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void NonPositiveCompanyIdFails()
    {
        var json = """{ "summary": "x", "cancelOrderIds": [], "bigInvestment": null, "orders": [ { "side": "Buy", "companyId": 0, "quantity": 1, "limitPrice": 1, "reason": "r" } ] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void BlankReasonFails()
    {
        var json = """{ "summary": "x", "cancelOrderIds": [], "bigInvestment": null, "orders": [ { "side": "Buy", "companyId": 1, "quantity": 1, "limitPrice": 1, "reason": "  " } ] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }

    [Fact]
    public void MoreThanMaxOrdersFails()
    {
        var orders = string.Join(",", Enumerable.Range(1, MaxOrders + 1).Select(id =>
            $$"""{ "side": "Buy", "companyId": {{id}}, "quantity": 1, "limitPrice": 1, "reason": "r" }"""));
        var json = $$"""{ "summary": "x", "cancelOrderIds": [], "bigInvestment": null, "orders": [ {{orders}} ] }""";
        Assert.False(AiDecisionJson.TryParse(json, MaxOrders, out _, out _));
    }
}
