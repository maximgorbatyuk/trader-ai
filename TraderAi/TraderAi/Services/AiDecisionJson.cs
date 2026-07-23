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
        => TryParseCore(content, maxOrders, 0, 0, false, out decision, out error);

    public static bool TryParse(
        string content,
        int maxOrders,
        int maxPredictions,
        int requiredPredictionHorizon,
        out AiTradeDecision? decision,
        out string? error)
        => TryParseCore(
            content,
            maxOrders,
            maxPredictions,
            requiredPredictionHorizon,
            true,
            out decision,
            out error);

    private static bool TryParseCore(
        string content,
        int maxOrders,
        int maxPredictions,
        int requiredPredictionHorizon,
        bool requirePredictions,
        out AiTradeDecision? decision,
        out string? error)
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

            if (requirePredictions)
            {
                if (!document.RootElement.TryGetProperty("predictions", out var predictions)
                    || predictions.ValueKind != JsonValueKind.Array)
                {
                    error = "A predictions array is required.";
                    return false;
                }

                foreach (var prediction in predictions.EnumerateArray())
                {
                    if (!prediction.TryGetProperty("direction", out var direction)
                        || direction.ValueKind != JsonValueKind.String
                        || direction.GetString() is not ("Up" or "Down"))
                    {
                        error = "Each prediction direction must be exactly Up or Down.";
                        return false;
                    }
                }
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

        if (requirePredictions)
        {
            if (parsed.Predictions.Length > maxPredictions)
            {
                error = $"A decision may contain at most {maxPredictions} predictions.";
                return false;
            }

            if (parsed.Predictions
                .Select(prediction => (prediction.CompanyId, prediction.HorizonCycles))
                .Distinct()
                .Count() != parsed.Predictions.Length)
            {
                error = "Each prediction companyId and horizonCycles pair must be unique.";
                return false;
            }

            foreach (var prediction in parsed.Predictions)
            {
                if (prediction.CompanyId <= 0)
                {
                    error = "Each prediction companyId must be positive.";
                    return false;
                }

                if (prediction.Confidence is < 0.5m or > 1m)
                {
                    error = "Each prediction confidence must be between 0.5 and 1.";
                    return false;
                }

                if (prediction.HorizonCycles <= 0 || prediction.HorizonCycles != requiredPredictionHorizon)
                {
                    error = $"Each prediction horizonCycles must equal the configured horizon of {requiredPredictionHorizon}.";
                    return false;
                }

                if (prediction.TargetPrice is <= 0m)
                {
                    error = "Each prediction targetPrice must be positive when provided.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(prediction.Reason))
                {
                    error = "Each prediction requires a non-empty reason.";
                    return false;
                }
            }
        }

        if (parsed.BigInvestment is { } investment)
        {
            if (investment.CompanyId <= 0)
            {
                error = "The bigInvestment companyId must be positive.";
                return false;
            }

            if (requirePredictions)
            {
                if (investment.Shares is not > 0 || investment.Amount is not null)
                {
                    error = "A fresh bigInvestment must contain positive shares and no amount.";
                    return false;
                }
            }
            else if (investment.Amount is not > 0m && investment.Shares is not > 0)
            {
                error = "The bigInvestment amount or shares must be positive.";
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

            // A percent at or below -100 would resolve to a zero or negative price; any other value is safe because
            // the backend clamps the resolved price onto the allowed band.
            if (order.PriceOffsetPercent <= -100m)
            {
                error = "Each order priceOffsetPercent must be greater than -100.";
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

// Some models return "summary" as an array of clause strings even though the schema asks for one string, because
// the instruction requests a few conclusion clauses. Accepting that shape and joining it keeps an otherwise-valid
// decision instead of discarding every order over a cosmetic field; a non-string element still fails so orders are
// never coerced from malformed data.
public sealed class SummaryJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() ?? string.Empty;
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var clauses = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    return string.Join("; ", clauses);
                }

                if (reader.TokenType != JsonTokenType.String)
                {
                    throw new JsonException("Each summary array element must be a string.");
                }

                var clause = reader.GetString();
                if (!string.IsNullOrWhiteSpace(clause))
                {
                    clauses.Add(clause.Trim());
                }
            }

            throw new JsonException("The summary array was not terminated.");
        }

        throw new JsonException("The summary must be a string or an array of strings.");
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
