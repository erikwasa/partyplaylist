using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;

namespace PartyPlaylist.Api;

public sealed class TableStorageService
{
    private const string RoomsTableName = "Rooms";
    private const string QueueItemsTableName = "QueueItems";
    private const string SpotifyTokensTableName = "SpotifyTokens";
    private const string OAuthStatesTableName = "OAuthStates";

    private readonly TableClient _rooms;
    private readonly TableClient _queueItems;
    private readonly TableClient _spotifyTokens;
    private readonly TableClient _oauthStates;

    public TableStorageService(IConfiguration configuration)
    {
        var connectionString = configuration["Tables:ConnectionString"]
            ?? configuration["Tables__ConnectionString"]
            ?? configuration["AzureWebJobsStorage"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Missing Table Storage connection string. Set Tables__ConnectionString or AzureWebJobsStorage.");
        }

        _rooms = new TableClient(connectionString, RoomsTableName);
        _queueItems = new TableClient(connectionString, QueueItemsTableName);
        _spotifyTokens = new TableClient(connectionString, SpotifyTokensTableName);
        _oauthStates = new TableClient(connectionString, OAuthStatesTableName);

        _rooms.CreateIfNotExists();
        _queueItems.CreateIfNotExists();
        _spotifyTokens.CreateIfNotExists();
        _oauthStates.CreateIfNotExists();
    }

    public async Task CreateOAuthStateAsync(string state)
    {
        await _oauthStates.AddEntityAsync(new OAuthStateEntity
        {
            RowKey = state,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    public async Task<bool> TryConsumeOAuthStateAsync(string state)
    {
        try
        {
            var response = await _oauthStates.GetEntityAsync<OAuthStateEntity>("state", state);
            var entity = response.Value;
            await _oauthStates.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, ETag.All);

            return entity.CreatedAt >= DateTimeOffset.UtcNow.AddMinutes(-10);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    public async Task UpsertHostTokenAsync(HostTokenEntity token)
    {
        await _spotifyTokens.UpsertEntityAsync(token, TableUpdateMode.Replace);
    }

    public async Task<HostTokenEntity?> GetHostTokenAsync()
    {
        try
        {
            var response = await _spotifyTokens.GetEntityAsync<HostTokenEntity>("host", "default");
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<RoomEntity> CreateRoomAsync(string hostSpotifyUserId, string? activeDeviceId)
    {
        var room = new RoomEntity
        {
            RowKey = Guid.NewGuid().ToString("N")[..8],
            HostSpotifyUserId = hostSpotifyUserId,
            ActiveDeviceId = activeDeviceId,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = "active"
        };

        await _rooms.AddEntityAsync(room);
        return room;
    }

    public async Task<RoomEntity?> GetRoomAsync(string roomId)
    {
        try
        {
            var response = await _rooms.GetEntityAsync<RoomEntity>("room", roomId);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<QueueItemEntity> AddQueueItemAsync(string roomId, AddQueueItemRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new QueueItemEntity
        {
            PartitionKey = GetRoomQueuePartitionKey(roomId),
            RowKey = $"{now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}",
            TrackUri = request.TrackUri!.Trim(),
            TrackName = request.TrackName!.Trim(),
            ArtistName = request.ArtistName!.Trim(),
            RequestedBy = request.RequestedBy!.Trim(),
            Votes = 0,
            Status = QueueItemStatuses.Waiting,
            CreatedAt = now
        };

        await _queueItems.AddEntityAsync(item);
        return item;
    }

    public async Task<IReadOnlyList<QueueItemEntity>> GetQueueAsync(string roomId)
    {
        var items = new List<QueueItemEntity>();
        await foreach (var item in _queueItems.QueryAsync<QueueItemEntity>(filter: $"PartitionKey eq '{GetRoomQueuePartitionKey(roomId)}'"))
        {
            items.Add(item);
        }

        return items.OrderBy(item => item.RowKey).ToList();
    }

    public async Task<QueueItemEntity?> GetNextWaitingQueueItemAsync(string roomId)
    {
        var queue = await GetQueueAsync(roomId);
        return queue.FirstOrDefault(item => item.Status == QueueItemStatuses.Waiting);
    }

    public async Task UpdateQueueItemStatusAsync(QueueItemEntity item, string status)
    {
        item.Status = status;
        await _queueItems.UpdateEntityAsync(item, ETag.All, TableUpdateMode.Replace);
    }

    private static string GetRoomQueuePartitionKey(string roomId) => $"room_{roomId}";
}

public sealed class SpotifyService
{
    private static readonly string[] RequiredScopes =
    [
        "user-modify-playback-state",
        "user-read-playback-state",
        "user-read-currently-playing"
    ];

    private readonly HttpClient _httpClient;
    private readonly TableStorageService _storage;
    private readonly IConfiguration _configuration;

    public SpotifyService(HttpClient httpClient, TableStorageService storage, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _storage = storage;
        _configuration = configuration;
    }

    public string BuildAuthorizationUrl(string state)
    {
        var clientId = GetRequiredSetting("Spotify:ClientId");
        var redirectUri = GetRequiredSetting("Spotify:RedirectUri");
        var scope = string.Join(' ', RequiredScopes);

        return "https://accounts.spotify.com/authorize"
            + "?response_type=code"
            + $"&client_id={Uri.EscapeDataString(clientId)}"
            + $"&scope={Uri.EscapeDataString(scope)}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + $"&state={Uri.EscapeDataString(state)}";
    }

    public async Task<HostTokenEntity> ExchangeCodeForTokenAsync(string code)
    {
        var token = await SendTokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = GetRequiredSetting("Spotify:RedirectUri")
        });

        var spotifyUserId = await GetCurrentSpotifyUserIdAsync(token.AccessToken);
        var entity = new HostTokenEntity
        {
            SpotifyUserId = spotifyUserId,
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken ?? string.Empty,
            TokenType = token.TokenType,
            Scope = token.Scope,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn)
        };

        await _storage.UpsertHostTokenAsync(entity);
        return entity;
    }

    public async Task<HostTokenEntity> GetValidHostTokenAsync()
    {
        var existing = await _storage.GetHostTokenAsync()
            ?? throw new InvalidOperationException("No Spotify host token exists yet. Visit /api/auth/login first.");

        if (existing.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return existing;
        }

        if (string.IsNullOrWhiteSpace(existing.RefreshToken))
        {
            throw new InvalidOperationException("Spotify token expired and no refresh token is available. Visit /api/auth/login again.");
        }

        var refreshed = await SendTokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = existing.RefreshToken
        });

        existing.AccessToken = refreshed.AccessToken;
        existing.RefreshToken = string.IsNullOrWhiteSpace(refreshed.RefreshToken) ? existing.RefreshToken : refreshed.RefreshToken;
        existing.TokenType = refreshed.TokenType;
        existing.Scope = refreshed.Scope ?? existing.Scope;
        existing.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn);

        await _storage.UpsertHostTokenAsync(existing);
        return existing;
    }

    public async Task<IReadOnlyList<TrackSearchResult>> SearchTracksAsync(string query, int limit = 10)
    {
        var token = await GetValidHostTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.spotify.com/v1/search?type=track&limit={limit}&q={Uri.EscapeDataString(query)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        using var response = await _httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Spotify search failed: {(int)response.StatusCode} {body}");
        }

        using var document = JsonDocument.Parse(body);
        var results = new List<TrackSearchResult>();

        foreach (var item in document.RootElement.GetProperty("tracks").GetProperty("items").EnumerateArray())
        {
            var artists = item.GetProperty("artists")
                .EnumerateArray()
                .Select(artist => artist.GetProperty("name").GetString())
                .Where(name => !string.IsNullOrWhiteSpace(name));

            string? imageUrl = null;
            if (item.TryGetProperty("album", out var album)
                && album.TryGetProperty("images", out var images)
                && images.ValueKind == JsonValueKind.Array
                && images.GetArrayLength() > 0)
            {
                imageUrl = images[0].GetProperty("url").GetString();
            }

            results.Add(new TrackSearchResult
            {
                TrackUri = item.GetProperty("uri").GetString() ?? string.Empty,
                TrackName = item.GetProperty("name").GetString() ?? string.Empty,
                ArtistName = string.Join(", ", artists),
                AlbumImageUrl = imageUrl,
                DurationMs = item.GetProperty("duration_ms").GetInt32()
            });
        }

        return results;
    }

    public async Task QueueTrackAsync(string accessToken, string trackUri, string? deviceId)
    {
        var url = $"https://api.spotify.com/v1/me/player/queue?uri={Uri.EscapeDataString(trackUri)}";
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            url += $"&device_id={Uri.EscapeDataString(deviceId)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Spotify queue request failed: {(int)response.StatusCode} {body}");
        }
    }

    private async Task<SpotifyTokenResponse> SendTokenRequestAsync(Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BuildBasicAuthValue());
        request.Content = new FormUrlEncodedContent(form);

        using var response = await _httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Spotify token request failed: {(int)response.StatusCode} {body}");
        }

        return JsonSerializer.Deserialize<SpotifyTokenResponse>(body)
            ?? throw new InvalidOperationException("Spotify token response could not be parsed.");
    }

    private async Task<string> GetCurrentSpotifyUserIdAsync(string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Spotify profile request failed: {(int)response.StatusCode} {body}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Spotify profile response did not include an id.");
    }

    private string BuildBasicAuthValue()
    {
        var clientId = GetRequiredSetting("Spotify:ClientId");
        var clientSecret = GetRequiredSetting("Spotify:ClientSecret");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
    }

    private string GetRequiredSetting(string key)
    {
        return _configuration[key]
            ?? _configuration[key.Replace(':', '_')]
            ?? throw new InvalidOperationException($"Missing configuration value '{key}'.");
    }
}
