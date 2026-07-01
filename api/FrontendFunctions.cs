using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Hosting;

namespace PartyPlaylist.Api;

public sealed class FrontendFunctions
{
    private static readonly IReadOnlyDictionary<string, string> AllowedAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["app.js"] = "text/javascript; charset=utf-8",
        ["styles.css"] = "text/css; charset=utf-8"
    };

    private readonly string _wwwrootPath;

    public FrontendFunctions(IHostEnvironment environment)
    {
        _wwwrootPath = Path.Combine(environment.ContentRootPath, "wwwroot");
    }

    [Function("FrontendApp")]
    public async Task<HttpResponseData> App(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "app")] HttpRequestData request)
    {
        return await ServeFileAsync(request, "index.html", "text/html; charset=utf-8");
    }

    [Function("FrontendAsset")]
    public async Task<HttpResponseData> Asset(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "app/{fileName}")] HttpRequestData request,
        string fileName)
    {
        if (!AllowedAssets.TryGetValue(fileName, out var contentType))
        {
            return request.CreateResponse(HttpStatusCode.NotFound);
        }

        return await ServeFileAsync(request, fileName, contentType);
    }

    private async Task<HttpResponseData> ServeFileAsync(HttpRequestData request, string fileName, string contentType)
    {
        var path = Path.Combine(_wwwrootPath, fileName);
        if (!File.Exists(path))
        {
            return request.CreateResponse(HttpStatusCode.NotFound);
        }

        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", contentType);
        await response.WriteStringAsync(await File.ReadAllTextAsync(path));
        return response;
    }
}
