using Krissvia.Mobile.Client;
using Krissvia.Mobile.Services;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;

namespace Krissvia.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"));

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddMudServices();

        // Un seul serveur, un seul flux : tout est singleton, et le flux survit aux changements d'écran.
        builder.Services.AddSingleton<ConnexionSettings>();
        builder.Services.AddSingleton<IConnexion>(s => s.GetRequiredService<ConnexionSettings>());
        builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        builder.Services.AddSingleton<KrissviaApi>();
        builder.Services.AddSingleton<ThermostatsEnDirect>();
        builder.Services.AddSingleton<FluxEnDirect>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
