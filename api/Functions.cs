using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using QRCoder;

namespace PartyPlaylist.Api;

public sealed class HealthFunction
{
    [Function("Health")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData request)
    {
        return await request.JsonAsync(new { status = "ok", utc = DateTimeOffset.UtcNow });
    }
}

public sealed class AuthFunctions
{
    private readonly TableStorageService _storage;
    private readonly SpotifyService _spotify;

    public AuthFunctions(TableStorageService storage, SpotifyService spotify)
    {
        _storage = storage;
        _spotify = spotify;
    }

    [Function("SpotifyLogin")]
    public async Task<HttpResponseData> Login(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/login")] HttpRequestData request)
    {
        var state = Guid.NewGuid().ToString("N");
        await _storage.CreateOAuthStateAsync(state);

        var response = request.CreateResponse(HttpStatusCode.Found);
        response.Headers.Add("Location", _spotify.BuildAuthorizationUrl(state));
        return response;
    }

    [Function("SpotifyCallback")]
    public async Task<HttpResponseData> Callback(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/callback")] HttpRequestData request)
    {
        var query = request.ParseQuery();

        if (query.TryGetValue("error", out var error))
        {
            return await request.JsonAsync(new { error }, HttpStatusCode.BadRequest);
        }

        if (!query.TryGetValue("state", out var state) || !await _storage.TryConsumeOAuthStateAsync(state))
        {
            return await request.JsonAsync(new { error = "Invalid or expired OAuth state." }, HttpStatusCode.BadRequest);
        }

        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            return await request.JsonAsync(new { error = "Missing Spotify authorization code." }, HttpStatusCode.BadRequest);
        }

        var token = await _spotify.ExchangeCodeForTokenAsync(code);
        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "text/html; charset=utf-8");
        await response.WriteStringAsync($"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><title>Spotify connected</title></head>
            <body>
              <h1>Spotify connected</h1>
              <p>Host account <strong>{WebUtility.HtmlEncode(token.SpotifyUserId)}</strong> is ready.</p>
              <p>You can close this tab and create a party room.</p>
            </body>
            </html>
            """);
        return response;
    }
}

public sealed class SearchFunction
{
    private readonly SpotifyService _spotify;

    public SearchFunction(SpotifyService spotify)
    {
        _spotify = spotify;
    }

    [Function("SearchTracks")]
    public async Task<HttpResponseData> Search(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "search")] HttpRequestData request)
    {
        var query = request.ParseQuery();
        if (!query.TryGetValue("query", out var searchText) || string.IsNullOrWhiteSpace(searchText))
        {
            return await request.JsonAsync(new { error = "Missing query parameter." }, HttpStatusCode.BadRequest);
        }

        try
        {
            var results = await _spotify.SearchTracksAsync(searchText);
            return await request.JsonAsync(results);
        }
        catch (SpotifyApiException ex)
        {
            return await request.SpotifyErrorAsync(ex);
        }
    }
}

public sealed class DeviceFunctions
{
    private readonly SpotifyService _spotify;

    public DeviceFunctions(SpotifyService spotify)
    {
        _spotify = spotify;
    }

    [Function("GetDevices")]
    public async Task<HttpResponseData> GetDevices(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "devices")] HttpRequestData request)
    {
        try
        {
            var devices = await _spotify.GetAvailableDevicesAsync();
            return await request.JsonAsync(devices);
        }
        catch (SpotifyApiException ex)
        {
            return await request.SpotifyErrorAsync(ex);
        }
    }
}

public sealed class RoomFunctions
{
    private readonly TableStorageService _storage;
    private readonly SpotifyService _spotify;

    public RoomFunctions(TableStorageService storage, SpotifyService spotify)
    {
        _storage = storage;
        _spotify = spotify;
    }

    [Function("CreateRoom")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rooms")] HttpRequestData request)
    {
        var body = await request.ReadJsonAsync<CreateRoomRequest>() ?? new CreateRoomRequest();
        var hostToken = await _spotify.GetValidHostTokenAsync();
        var hostCode = HostCodeService.GenerateCode();
        var room = await _storage.CreateRoomAsync(hostToken.SpotifyUserId, body.ActiveDeviceId, HostCodeService.HashCode(hostCode));

        return await request.JsonAsync(ToRoomResponse(room, hostCode), HttpStatusCode.Created);
    }

    [Function("GetRoomQr")]
    public async Task<HttpResponseData> Qr(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rooms/{roomId}/qr")] HttpRequestData request,
        string roomId)
    {
        var room = await _storage.GetRoomAsync(roomId);
        if (room is null)
        {
            return await request.JsonAsync(new { error = "Room not found." }, HttpStatusCode.NotFound);
        }

        var guestLink = request.GetGuestLink(room.RowKey);
        using var qrCodeData = QRCodeGenerator.GenerateQrCode(guestLink, QRCodeGenerator.ECCLevel.Q);
        using var svgRenderer = new SvgQRCode(qrCodeData);
        var svg = svgRenderer.GetGraphic(8);

        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "image/svg+xml; charset=utf-8");
        await response.WriteStringAsync(svg);
        return response;
    }

    [Function("SetRoomDevice")]
    public async Task<HttpResponseData> SetDevice(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rooms/{roomId}/device")] HttpRequestData request,
        string roomId)
    {
        var room = await _storage.GetRoomAsync(roomId);
        if (room is null)
        {
            return await request.JsonAsync(new { error = "Room not found." }, HttpStatusCode.NotFound);
        }

        if (await request.RequireHostAsync(room) is { } denied)
        {
            return denied;
        }

        var body = await request.ReadJsonAsync<UpdateRoomDeviceRequest>() ?? new UpdateRoomDeviceRequest();
        var activeDeviceId = string.IsNullOrWhiteSpace(body.ActiveDeviceId) ? null : body.ActiveDeviceId.Trim();

        if (activeDeviceId is not null)
        {
            try
            {
                var devices = await _spotify.GetAvailableDevicesAsync();
                var selectedDevice = devices.FirstOrDefault(device => device.Id == activeDeviceId);
                if (selectedDevice is null)
                {
                    return await request.JsonAsync(new
                    {
                        error = "Selected Spotify device was not found. Refresh devices and try again."
                    }, HttpStatusCode.BadRequest);
                }

                if (selectedDevice.IsRestricted)
                {
                    return await request.JsonAsync(new
                    {
                        error = "Selected Spotify device is restricted and cannot be controlled through the Web API."
                    }, HttpStatusCode.BadRequest);
                }
            }
            catch (SpotifyApiException ex)
            {
                return await request.SpotifyErrorAsync(ex);
            }
        }

        room = await _storage.SetRoomActiveDeviceAsync(room, activeDeviceId);
        return await request.JsonAsync(ToRoomResponse(room));
    }

    private static object ToRoomResponse(RoomEntity room, string? hostCode = null) => new
    {
        roomId = room.RowKey,
        room.Status,
        room.ActiveDeviceId,
        hostCode,
        room.CreatedAt
    };
}

public sealed class QueueFunctions
{
    private readonly TableStorageService _storage;
    private readonly SpotifyService _spotify;

    public QueueFunctions(TableStorageService storage, SpotifyService spotify)
    {
        _storage = storage;
        _spotify = spotify;
    }

    [Function("GetQueue")]
    public async Task<HttpResponseData> GetQueue(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rooms/{roomId}/queue")] HttpRequestData request,
        string roomId)
    {
        if (await _storage.GetRoomAsync(roomId) is null)
        {
            return await request.JsonAsync(new { error = "Room not found." }, HttpStatusCode.NotFound);
        }

        var queue = await _storage.GetQueueAsync(roomId);
        return await request.JsonAsync(queue.Select(ToResponse));
    }

    [Function("AddQueueItem")]
    public async Task<HttpResponseData> AddQueueItem(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rooms/{roomId}/queue")] HttpRequestData request,
        string roomId)
    {
        if (await _storage.GetRoomAsync(roomId) is null)
        {
            return await request.JsonAsync(new { error = "Room not found." }, HttpStatusCode.NotFound);
        }

        var body = await request.ReadJsonAsync<AddQueueItemRequest>();
        if (body is null
            || string.IsNullOrWhiteSpace(body.TrackUri)
            || string.IsNullOrWhiteSpace(body.TrackName)
            || string.IsNullOrWhiteSpace(body.ArtistName)
            || string.IsNullOrWhiteSpace(body.RequestedBy))
        {
            return await request.JsonAsync(new { error = "trackUri, trackName, artistName, and requestedBy are required." }, HttpStatusCode.BadRequest);
        }

        var duplicate = await _storage.FindActiveDuplicateQueueItemAsync(roomId, body.TrackUri);
        if (duplicate is not null)
        {
            return await request.JsonAsync(new
            {
                error = "This track is already in the room queue.",
                duplicate = ToResponse(duplicate)
            }, HttpStatusCode.Conflict);
        }

        var item = await _storage.AddQueueItemAsync(roomId, body);
        return await request.JsonAsync(ToResponse(item), HttpStatusCode.Created);
    }

    [Function("QueueNext")]
    public async Task<HttpResponseData> QueueNext(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rooms/{roomId}/queue-next")] HttpRequestData request,
        string roomId)
    {
        var room = await _storage.GetRoomAsync(roomId);
        if (room is null)
        {
            return await request.JsonAsync(new { error = "Room not found." }, HttpStatusCode.NotFound);
        }

        if (await request.RequireHostAsync(room) is { } denied)
        {
            return denied;
        }

        var item = await _storage.GetNextWaitingQueueItemAsync(roomId);
        if (item is null)
        {
            return await request.JsonAsync(new { queued = false, message = "No waiting tracks." });
        }

        var token = await _spotify.GetValidHostTokenAsync();

        try
        {
            await _spotify.QueueTrackAsync(token.AccessToken, item.TrackUri, room.ActiveDeviceId);
            await _storage.UpdateQueueItemStatusAsync(item, QueueItemStatuses.QueuedToSpotify);

            return await request.JsonAsync(new { queued = true, item = ToResponse(item) });
        }
        catch (SpotifyApiException ex)
        {
            return await request.SpotifyErrorAsync(ex);
        }
    }

    [Function("PlayNext")]
    public async Task<HttpResponseData> PlayNext(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rooms/{roomId}/play-next")] HttpRequestData request,
        string roomId)
    {
        var room = await _storage.GetRoomAsync(roomId);
        if (room is null)
        {
            return await request.JsonAsync(new { error = "Room not found." }, HttpStatusCode.NotFound);
        }

        if (await request.RequireHostAsync(room) is { } denied)
        {
            return denied;
        }

        var item = await _storage.GetNextWaitingQueueItemAsync(roomId);
        if (item is null)
        {
            return await request.JsonAsync(new { played = false, message = "No waiting tracks." });
        }

        var token = await _spotify.GetValidHostTokenAsync();

        try
        {
            await _spotify.StartPlaybackAsync(token.AccessToken, item.TrackUri, room.ActiveDeviceId);
            await _storage.MarkCurrentPlayingAsPlayedAsync(roomId);
            await _storage.UpdateQueueItemStatusAsync(item, QueueItemStatuses.Playing);

            return await request.JsonAsync(new { played = true, item = ToResponse(item) });
        }
        catch (SpotifyApiException ex)
        {
            return await request.SpotifyErrorAsync(ex);
        }
    }

    [Function("RemoveQueueItem")]
    public async Task<HttpResponseData> RemoveQueueItem(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rooms/{roomId}/queue/{itemId}/remove")] HttpRequestData request,
        string roomId,
        string itemId)
    {
        var room = await _storage.GetRoomAsync(roomId);
        if (room is null)
        {
            return await request.JsonAsync(new { error = "Room not found." }, HttpStatusCode.NotFound);
        }

        if (await request.RequireHostAsync(room) is { } denied)
        {
            return denied;
        }

        var item = await _storage.GetQueueItemAsync(roomId, itemId);
        if (item is null)
        {
            return await request.JsonAsync(new { error = "Queue item not found." }, HttpStatusCode.NotFound);
        }

        await _storage.UpdateQueueItemStatusAsync(item, QueueItemStatuses.Removed);
        return await request.JsonAsync(new { removed = true, item = ToResponse(item) });
    }

    [Function("RequeueQueueItem")]
    public async Task<HttpResponseData> RequeueQueueItem(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rooms/{roomId}/queue/{itemId}/requeue")] HttpRequestData request,
        string roomId,
        string itemId)
    {
        var room = await _storage.GetRoomAsync(roomId);
        if (room is null)
        {
            return await request.JsonAsync(new { error = "Room not found." }, HttpStatusCode.NotFound);
        }

        if (await request.RequireHostAsync(room) is { } denied)
        {
            return denied;
        }

        var item = await _storage.GetQueueItemAsync(roomId, itemId);
        if (item is null)
        {
            return await request.JsonAsync(new { error = "Queue item not found." }, HttpStatusCode.NotFound);
        }

        var duplicate = await _storage.FindActiveDuplicateQueueItemAsync(roomId, item.TrackUri, item.RowKey);
        if (duplicate is not null)
        {
            return await request.JsonAsync(new
            {
                error = "Cannot requeue because this track is already active in the room queue.",
                duplicate = ToResponse(duplicate)
            }, HttpStatusCode.Conflict);
        }

        await _storage.UpdateQueueItemStatusAsync(item, QueueItemStatuses.Waiting);
        return await request.JsonAsync(new { requeued = true, item = ToResponse(item) });
    }

    private static object ToResponse(QueueItemEntity item) => new
    {
        id = item.RowKey,
        item.TrackUri,
        item.TrackName,
        item.ArtistName,
        item.RequestedBy,
        item.Votes,
        item.Status,
        item.CreatedAt
    };
}

internal static class HttpRequestDataExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<T?> ReadJsonAsync<T>(this HttpRequestData request)
    {
        if (request.Body is null || !request.Body.CanRead)
        {
            return default;
        }

        return await JsonSerializer.DeserializeAsync<T>(request.Body, JsonOptions);
    }

    public static async Task<HttpResponseData> JsonAsync(this HttpRequestData request, object body, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var response = request.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return response;
    }

    public static async Task<HttpResponseData> SpotifyErrorAsync(this HttpRequestData request, SpotifyApiException ex)
    {
        var noActiveDevice = string.Equals(ex.SpotifyReason, "NO_ACTIVE_DEVICE", StringComparison.OrdinalIgnoreCase);
        var statusCode = noActiveDevice ? HttpStatusCode.Conflict : HttpStatusCode.BadGateway;
        var hint = noActiveDevice
            ? "Open Spotify on the host device, start playback, refresh devices, select the device, then try Queue next again."
            : "Spotify rejected the request. Check the spotifyStatusCode and spotifyResponse details.";

        return await request.JsonAsync(new
        {
            error = noActiveDevice ? "No active Spotify device found." : "Spotify request failed.",
            ex.Operation,
            spotifyStatusCode = ex.StatusCode,
            spotifyReason = ex.SpotifyReason,
            spotifyMessage = ex.SpotifyMessage,
            spotifyResponse = ex.ResponseBody,
            hint
        }, statusCode);
    }

    public static async Task<HttpResponseData?> RequireHostAsync(this HttpRequestData request, RoomEntity room)
    {
        if (string.IsNullOrWhiteSpace(room.HostCodeHash))
        {
            return await request.JsonAsync(new
            {
                error = "This room was created before host controls were protected. Create a new room."
            }, HttpStatusCode.Forbidden);
        }

        if (!request.Headers.TryGetValues(HostCodeService.HeaderName, out var values))
        {
            return await request.JsonAsync(new { error = "Missing host authorization." }, HttpStatusCode.Forbidden);
        }

        var hostCode = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(hostCode) || !HostCodeService.VerifyCode(hostCode, room.HostCodeHash))
        {
            return await request.JsonAsync(new { error = "Invalid host authorization." }, HttpStatusCode.Forbidden);
        }

        return null;
    }

    public static string GetGuestLink(this HttpRequestData request, string roomId)
    {
        return $"{request.Url.Scheme}://{request.Url.Authority}/api/app?roomId={Uri.EscapeDataString(roomId)}";
    }

    public static IReadOnlyDictionary<string, string> ParseQuery(this HttpRequestData request)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var query = request.Url.Query;
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var key = Decode(pieces[0]);
            var value = pieces.Length == 2 ? Decode(pieces[1]) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace("+", " "));
}
