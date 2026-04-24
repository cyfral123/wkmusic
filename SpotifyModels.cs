using System;

namespace WKMusic;

public record SpotifyToken(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    DateTime ObtainedAt
)
{
    public bool IsExpired => DateTime.UtcNow >= ObtainedAt.AddSeconds(ExpiresIn - 30);
}

public record TrackInfo(
    string Id,
    string Title,
    string Artist,
    string Album,
    string? CoverUrl,
    int DurationMs,
    byte[]? CoverBytes = null
);

public record PlaybackState(
    TrackInfo? Track,
    bool IsPlaying,
    int ProgressMs
)
{
    public static PlaybackState Empty => new(null, false, 0);
}
