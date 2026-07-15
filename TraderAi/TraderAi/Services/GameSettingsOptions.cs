using Microsoft.Extensions.Options;

namespace TraderAi.Services;

public sealed class GameSettingsOptions<TOptions>(
    GameSettingsService settings,
    string sectionName) : IOptions<TOptions>
    where TOptions : class, new()
{
    public TOptions Value => settings.GetOptions<TOptions>(sectionName);
}
