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

    private static object ToResponse(GameSettingValue setting) => new
    {
        setting.Definition.Key,
        setting.Definition.Section,
        setting.Definition.Subsection,
        setting.Definition.Name,
        setting.Definition.Description,
        setting.Definition.ValueType,
        setting.Value,
    };

    public sealed record SettingsUpdateRequest(IReadOnlyDictionary<string, JsonElement>? Values);
}
