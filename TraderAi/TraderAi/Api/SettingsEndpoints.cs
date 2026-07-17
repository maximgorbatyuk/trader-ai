using System.Text.Json;
using TraderAi.Services;

namespace TraderAi.Api;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/settings", (GameSettingsService settings) =>
            Results.Ok(settings.GetAll().Select(ToResponse)));

        app.MapPut("/settings", async (
            SettingsUpdateRequest request,
            GameSettingsService settings,
            MarketCycleLock marketCycleLock,
            CancellationToken cancellationToken) =>
        {
            if (request.Values is null)
            {
                return Results.BadRequest(new
                {
                    error = "One or more settings are invalid.",
                    errors = new Dictionary<string, string[]> { ["values"] = ["Provide the settings to update."] },
                });
            }

            await marketCycleLock.Semaphore.WaitAsync(cancellationToken);
            try
            {
                await settings.UpdateAsync(request.Values, cancellationToken);
                return Results.Ok(settings.GetAll().Select(ToResponse));
            }
            catch (GameSettingsValidationException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Message,
                    errors = exception.Errors,
                });
            }
            finally
            {
                marketCycleLock.Semaphore.Release();
            }
        });
    }

    private static object ToResponse(GameSettingValue setting)
    {
        // A secret's stored value is never returned; the client only learns whether one is set so it can render a
        // "configured" hint and treat a blank submission as "keep the current key".
        var isSecret = setting.Definition.ValueType == GameSettingValueType.Secret;
        var hasValue = setting.Value.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(setting.Value.GetString());
        return new
        {
            setting.Definition.Key,
            setting.Definition.Section,
            setting.Definition.Subsection,
            setting.Definition.Name,
            setting.Definition.Description,
            setting.Definition.ValueType,
            Value = isSecret ? (object)string.Empty : setting.Value,
            HasValue = hasValue,
        };
    }

    public sealed record SettingsUpdateRequest(IReadOnlyDictionary<string, JsonElement>? Values);
}
