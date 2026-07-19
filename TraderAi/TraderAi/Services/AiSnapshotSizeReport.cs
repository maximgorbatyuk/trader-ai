using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TraderAi.Services;

// Diagnostic-only breakdown of how large each AI snapshot section is, plus the system prompt, so the input sent to a
// provider can be tuned against a model's context window. Character counts are exact; token counts are a rough
// heuristic (chars / CharsPerToken) that should be calibrated against a provider's measured prompt_tokens.
public static class AiSnapshotSizeReport
{
    // Mirrors AiTradingPromptBuilder's user-message serializer so the reported sizes match what is actually sent.
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

    // Mixed prose (docs) and dense numeric JSON land near this ratio; the coordinator also logs the provider's real
    // prompt_tokens so the divisor can be corrected per provider.
    private const double CharsPerToken = 3.5;

    public static string Build(AiMarketSnapshot snapshot, string systemMessage, string userMessage)
    {
        var builder = new StringBuilder();
        builder.Append("AI snapshot size (participant ").Append(snapshot.ParticipantId)
            .Append(", cycle ").Append(snapshot.Market.CycleNumber).AppendLine("):");

        AppendLine(builder, "system(docs+prompt)", systemMessage.Length);
        AppendSection(builder, "companies", snapshot.Companies);
        AppendSection(builder, "participant.holdings", snapshot.Participant.Holdings);
        AppendSection(builder, "participant.openOrders", snapshot.Participant.OpenOrders);
        AppendSection(builder, "sentimentHistory", snapshot.SentimentHistory);
        AppendSection(builder, "capitalizationHistory", snapshot.CapitalizationHistory);
        AppendSection(builder, "bigInvestmentOpportunities", snapshot.BigInvestmentOpportunities);
        AppendSection(builder, "industries", snapshot.Industries);
        AppendSection(builder, "recentApplicationFeedback", snapshot.RecentApplicationFeedback);
        AppendLine(builder, "user(total)", userMessage.Length);
        AppendLine(builder, "TOTAL(system+user)", systemMessage.Length + userMessage.Length);
        return builder.ToString();
    }

    private static void AppendSection<T>(StringBuilder builder, string label, T section)
        => AppendLine(builder, label, JsonSerializer.Serialize(section, SerializerOptions).Length);

    private static void AppendLine(StringBuilder builder, string label, int chars)
        => builder.Append("  ").Append(label.PadRight(28)).Append(chars).Append(" chars  ~")
            .Append((int)Math.Round(chars / CharsPerToken)).AppendLine(" tok");
}
