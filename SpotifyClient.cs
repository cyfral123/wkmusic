using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WKMusic;

public class SpotifyClient(SpotifyAuth auth, TokenStorage storage, string clientId, HttpClient? http = null)
    : IMusicClient
{
    private const string ApiBase = "https://api.spotify.com/v1";

    private readonly HttpClient _http = http ?? new HttpClient();
    private readonly string _clientId = clientId;

    private SpotifyToken? _token;
    
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _token = storage.Load();

        if (_token is null)
        {
            _token = await auth.AuthorizeAsync(_clientId, ct);
            storage.Save(_token);
        }
        else if (_token.IsExpired)
        {
            _token = await auth.RefreshAsync(_clientId, _token, ct);
            storage.Save(_token);
        }
    }

    public async Task<PlaybackState> GetPlaybackStateAsync(CancellationToken ct = default)
    {
        var json = await GetAsync("/me/player", ct);
        if (json is null) return PlaybackState.Empty;

        var root = JObject.Parse(json);
        var item = root["item"];
        if (item == null || item.Type == JTokenType.Null)
            return PlaybackState.Empty;

        var track = ParseTrack(item);
        var isPlaying = root["is_playing"]!.Value<bool>();
        var progressMs = root["progress_ms"]!.Value<int>();

        return new PlaybackState(track, isPlaying, progressMs);
    }
    
    public Task PlayAsync(CancellationToken ct = default) => PutAsync("/me/player/play", ct);
    public Task PauseAsync(CancellationToken ct = default) => PutAsync("/me/player/pause", ct);
    public Task NextAsync(CancellationToken ct = default) => PostAsync("/me/player/next", ct);
    public Task PreviousAsync(CancellationToken ct = default) => PostAsync("/me/player/previous", ct);

    public Task SeekAsync(int positionMs, CancellationToken ct = default) =>
        PutAsync($"/me/player/seek?position_ms={positionMs}", ct);

    public Task SetVolumeAsync(int volumePercent, CancellationToken ct = default) =>
        PutAsync($"/me/player/volume?volume_percent={Math.Clamp(volumePercent, 0, 100)}", ct);

    private async Task<string?> GetAsync(string path, CancellationToken ct)
    {
        await EnsureTokenValidAsync(ct);

        var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token!.AccessToken);

        var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await ForceRefreshAsync(ct);
            return await GetAsync(path, ct);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private async Task PutAsync(string path, CancellationToken ct)
    {
        await EnsureTokenValidAsync(ct);

        var request = new HttpRequestMessage(HttpMethod.Put, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token!.AccessToken);
        request.Content = new StringContent("", Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await ForceRefreshAsync(ct);
            await PutAsync(path, ct);
            return;
        }

        if (response.StatusCode != HttpStatusCode.NoContent)
            response.EnsureSuccessStatusCode();
    }

    private async Task PostAsync(string path, CancellationToken ct)
    {
        await EnsureTokenValidAsync(ct);

        var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token!.AccessToken);
        request.Content = new StringContent("", Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await ForceRefreshAsync(ct);
            await PostAsync(path, ct);
            return;
        }

        if (response.StatusCode != HttpStatusCode.NoContent)
            response.EnsureSuccessStatusCode();
    }

    private async Task EnsureTokenValidAsync(CancellationToken ct)
    {
        if (_token is null)
            throw new InvalidOperationException("Client not initialized. Call InitializeAsync first.");

        if (_token.IsExpired)
            await ForceRefreshAsync(ct);
    }

    private async Task ForceRefreshAsync(CancellationToken ct)
    {
        _token = await auth.RefreshAsync(_clientId, _token!, ct);
        storage.Save(_token);
    }
    
    private static TrackInfo ParseTrack(JToken item)
    {
        var id = item["id"]!.Value<string>()!;
        var title = item["name"]!.Value<string>()!;
        var artists = item["artists"]!.Select(a => a["name"]!.Value<string>());
        var artist = string.Join(", ", artists);
        var album = item["album"]!["name"]!.Value<string>()!;
        var duration = item["duration_ms"]!.Value<int>();

        var images = item["album"]!["images"];
        var coverUrl = images?.FirstOrDefault()?["url"]?.Value<string>();

        return new TrackInfo(id, title, artist, album, coverUrl, duration);
    }
}