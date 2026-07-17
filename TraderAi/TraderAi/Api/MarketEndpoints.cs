namespace TraderAi.Api;

public static partial class MarketEndpoints
{
    public static void MapMarketEndpoints(this WebApplication app)
    {
        app.MapCompanyEndpoints();
        app.MapMarketControlEndpoints();
        app.MapParticipantEndpoints();
        app.MapPlayerEndpoints();
        app.MapTradingEndpoints();
        app.MapNewsEndpoints();
        app.MapLoanEndpoints();
    }
}
