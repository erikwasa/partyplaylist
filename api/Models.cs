using System.Text.Json.Serialization;
using Azure;
using Azure.Data.Tables;

namespace PartyPlaylist.Api;

public static class QueueItemStatuses
{
    public const string Waiting = "waiting";
    public const string QueuedToSpotify = "queued_to_spotify";
    public const string Playing = "playing";
    public const string Played = "played";
    public const string Removed = "removed";
}

public sealed class CreateRoomRequest
{
    public string? ActiveDeviceId { get; set; }
}

public sealed class AddQueueItemRequest
{
    public string? TrackUri { get; set; }
    public string? TrackName { get; set; }
    public string? ArtistName { get; set; }
    public string? RequestedBy { get; set; }
}

public sealed class TrackSearchResult
{
    public string TrackUri { get; init; } = string.Empty;
    public string TrackName { get; init; } = string.Empty;
    public string ArtistName { get; init; } = string.Empty;
    public string? AlbumImageUrl { get; init; }
    public int DurationMs { get; init; }
}

public sealed class SpotifyTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }
}

public sealed class RoomEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "room";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string HostSpotifyUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = "active";
    public string? ActiveDeviceId { get; set; }
}

public sealed class QueueItemEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string TrackUri { get; set; } = string.Empty;
    public string TrackName { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public int Votes { get; set; }
    public string Status { get; set; } = QueueItemStatuses.Waiting;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class HostTokenEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "host";
    public string RowKey { get; set; } = "default";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string SpotifyUserId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public string TokenType { get; set; } = "Bearer";
    public string? Scope { get; set; }
}

public sealed class OAuthStateEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "state";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
