using System.Text.Json;
using System.Text.Json.Serialization;

namespace TraderAi.Services;

// Strict deserialization of the assistant content into a trade decision. It rejects Markdown fences, surrounding
// prose, unknown properties, integer enum values, and out-of-range fields, so a malformed response can never be
// silently coerced into orders. This options object is dedicated to AI content and must not leak into ordinary
// API serialization.
public static class AiDecisionJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public static bool TryParse(string content, int maxOrders, out AiTradeDecision? decision, out string? error)
    {
        decision = null;
        error = null;

        AiTradeDecision? parsed;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
            parsed = JsonSerializer.Deserialize<AiTradeDecision>(content, Options);
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("cancelOrderIds", out var cancelOrderIds)
                || cancelOrderIds.ValueKind != JsonValueKind.Array)
            {
                error = "A cancelOrderIds array is required.";
                return false;
            }

            if (!document.RootElement.TryGetProperty("bigInvestment", out var bigInvestment)
                || bigInvestment.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
            {
                error = "A bigInvestment object or null is required.";
                return false;
            }
        }

        if (parsed is null)
        {
            error = "The response was null.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Summary))
        {
            error = "A non-empty summary is required.";
            return false;
        }

        if (parsed.Orders is null)
        {
            error = "An orders array is required.";
            return false;
        }

        if (parsed.Orders.Length > maxOrders)
        {
            error = $"A decision may contain at most {maxOrders} orders.";
            return false;
        }

        if (parsed.CancelOrderIds.Length > maxOrders)
        {
            error = $"A decision may contain at most {maxOrders} cancelOrderIds.";
            return false;
        }

        if (parsed.CancelOrderIds.Distinct().Count() != parsed.CancelOrderIds.Length)
        {
            error = "Each cancelOrderIds value must be unique.";
            return false;
        }

        if (parsed.CancelOrderIds.Any(orderId => orderId <= 0))
        {
            error = "Each cancelOrderIds value must be positive.";
            return false;
        }

        if (parsed.BigInvestment is { } investment)
        {
            if (investment.CompanyId <= 0)
            {
                error = "The bigInvestment companyId must be positive.";
                return false;
            }

            if (investment.Amount <= 0m)
            {
                error = "The bigInvestment amount must be positive.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(investment.Reason))
            {
                error = "The bigInvestment requires a non-empty reason.";
                return false;
            }
        }

        foreach (var order in parsed.Orders)
        {
            if (order.CompanyId <= 0)
            {
                error = "Each order companyId must be positive.";
                return false;
            }

            if (order.Quantity <= 0)
            {
                error = "Each order quantity must be positive.";
                return false;
            }

            if (order.LimitPrice <= 0m)
            {
                error = "Each order limitPrice must be positive.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(order.Reason))
            {
                error = "Each order requires a non-empty reason.";
                return false;
            }
        }

        decision = parsed;
        return true;
    }
}
