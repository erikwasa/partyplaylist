using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PartyPlaylist.Api;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddHttpClient<SpotifyService>();
        services.AddSingleton<TableStorageService>();
    })
    .Build();

await host.RunAsync();
