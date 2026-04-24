using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WKMusic;

public class SpotifyAuth(string workerUrl, string clientSecret, HttpClient? http = null)
{
    private const string TokenUrl = "https://accounts.spotify.com/api/token";

    private readonly HttpClient _http = http ?? new HttpClient();
    private readonly string _workerUrl = workerUrl.TrimEnd('/');
    private readonly string _clientSecret = clientSecret;

    public async Task<SpotifyToken> AuthorizeAsync(string clientId, CancellationToken ct = default)
    {
        var redirectUri = $"{_workerUrl}/callback";
        var result = await BrowserSetup.RunAsync(
            _workerUrl,
            (id, _, code) => ExchangeCodeAsync(id, code, redirectUri, ct),
            initialClientId: clientId,
            ct: ct);

        if (result is null)
            throw new OperationCanceledException("Spotify authorization cancelled.");

        return result.Token;
    }

    public async Task<SpotifyToken> RefreshAsync(string clientId, SpotifyToken token, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = token.RefreshToken,
            ["client_id"] = clientId,
            ["client_secret"] = _clientSecret,
        };

        var response = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Token refresh failed: {json}");

        var obj = JObject.Parse(json);
        return new SpotifyToken(
            AccessToken: obj["access_token"]!.Value<string>()!,
            RefreshToken: obj["refresh_token"]?.Value<string>() ?? token.RefreshToken,
            ExpiresIn: obj["expires_in"]!.Value<int>(),
            ObtainedAt: DateTime.UtcNow
        );
    }

    internal async Task<SpotifyToken> ExchangeCodeAsync(
        string clientId, string code, string redirectUri, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["client_secret"] = _clientSecret,
        };

        var response = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Token exchange failed: {json}");

        var obj = JObject.Parse(json);
        return new SpotifyToken(
            AccessToken: obj["access_token"]!.Value<string>()!,
            RefreshToken: obj["refresh_token"]?.Value<string>() ?? string.Empty,
            ExpiresIn: obj["expires_in"]!.Value<int>(),
            ObtainedAt: DateTime.UtcNow
        );
    }
}