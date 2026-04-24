using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WKMusic;

internal static class BrowserSetup
{
    public sealed record SetupResult(string ClientId, string ClientSecret, SpotifyToken Token);

    public static async Task<SetupResult?> RunAsync(
        string workerUrl,
        Func<string, string, string, Task<SpotifyToken>> exchangeCode,
        string initialClientId = "",
        string initialClientSecret = "",
        CancellationToken ct = default)
    {
        var port = FindFreePort();
        var redirectUri = $"{workerUrl}/callback";

        string? credId = NullIfEmpty(initialClientId);
        string? credSecret = NullIfEmpty(initialClientSecret);

        var tcs = new TaskCompletionSource<SetupResult?>();
        ct.Register(() => tcs.TrySetResult(null));

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var state = new FlowState { CredId = credId, CredSecret = credSecret, ExchangeCode = exchangeCode };

        _ = Task.Run(async () =>
        {
            try
            {
                while (!tcs.Task.IsCompleted)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); }
                    catch { break; }
                    _ = Task.Run(() => Handle(ctx, port, redirectUri, state, tcs));
                }
            }
            catch { }
            finally { listener.Stop(); }
        });

        OpenBrowser($"http://localhost:{port}/");
        Plugin.Logger.LogInfo($"WKMusic: setup → http://localhost:{port}/");

        return await tcs.Task;
    }

    private sealed class FlowState
    {
        public string? CredId;
        public string? CredSecret;
        public string? OAuthState;
        public Func<string, string, string, Task<SpotifyToken>>? ExchangeCode;
    }

    private static async Task Handle(
        HttpListenerContext ctx,
        int port,
        string redirectUri,
        FlowState s,
        TaskCompletionSource<SetupResult?> tcs)
    {
        var req  = ctx.Request;
        var res  = ctx.Response;
        var path = req.Url!.AbsolutePath;

        try
        {
            if (req.HttpMethod == "GET" && path == "/")
            {
                if (s.CredId != null && s.CredSecret != null)
                {
                    s.OAuthState = $"{Guid.NewGuid():N}:{port}";
                    Redirect(res, BuildAuthUrl(s.CredId, s.OAuthState, redirectUri));
                }
                else
                {
                    await WriteHtml(res, PageCredentials());
                }
                return;
            }

            if (req.HttpMethod == "POST" && path == "/submit")
            {
                var form = await ReadForm(req);
                form.TryGetValue("clientId",     out var id);
                form.TryGetValue("clientSecret", out var secret);

                id     = id?.Trim()     ?? "";
                secret = secret?.Trim() ?? "";

                var err = ValidateCredentials(id, secret)
                       ?? await VerifyWithSpotifyAsync(id, secret);
                if (err != null)
                {
                    await WriteHtml(res, PageCredentials(id, err));
                    return;
                }

                s.CredId     = id;
                s.CredSecret = secret;
                s.OAuthState = $"{Guid.NewGuid():N}:{port}";

                Redirect(res, BuildAuthUrl(s.CredId, s.OAuthState, redirectUri));
                return;
            }

            if (req.HttpMethod == "GET" && path == "/callback")
            {
                var q     = req.QueryString;
                var code  = q["code"];
                var state = q["state"];
                var error = q["error"];

                if (!string.IsNullOrEmpty(error))
                {
                    await WriteHtml(res, PageError($"Spotify error: {error}"));
                    tcs.TrySetResult(null);
                    return;
                }

                if (state != s.OAuthState)
                {
                    await WriteHtml(res, PageError("State mismatch — please try again."));
                    tcs.TrySetResult(null);
                    return;
                }

                if (!string.IsNullOrEmpty(code) && s.CredId != null && s.CredSecret != null && s.ExchangeCode != null)
                {
                    try
                    {
                        var token = await s.ExchangeCode(s.CredId, s.CredSecret, code);
                        await WriteHtml(res, PageSuccess());
                        tcs.TrySetResult(new SetupResult(s.CredId, s.CredSecret, token));
                    }
                    catch (Exception ex)
                    {
                        await WriteHtml(res, PageError($"Token exchange failed: {ex.Message}"));
                        tcs.TrySetResult(null);
                    }
                    return;
                }

                await WriteHtml(res, PageError("Missing authorization code."));
                tcs.TrySetResult(null);
                return;
            }

            res.StatusCode = 404;
            res.Close();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"WKMusic: BrowserSetup handler error: {ex.Message}");
            try { res.Abort(); } catch { }
        }
    }

    private static string? ValidateCredentials(string id, string secret)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret))
            return "Both fields are required.";
        return null;
    }

    private static readonly HttpClient _http = new();

    private static async Task<string?> VerifyWithSpotifyAsync(string clientId, string clientSecret)
    {
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "client_credentials",
                ["client_id"]     = clientId,
                ["client_secret"] = clientSecret,
            });
            var resp = await _http.PostAsync("https://accounts.spotify.com/api/token", form);
            if (resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync();
            var err  = JObject.Parse(body)["error"]?.Value<string>() ?? "";

            return err == "invalid_client"
                ? "Invalid Client ID or Client Secret — check your Spotify Dashboard."
                : $"Spotify returned: {err}";
        }
        catch
        {
            return null;
        }
    }

    private static readonly string[] Scopes =
    {
        "user-read-playback-state",
        "user-modify-playback-state",
        "user-read-currently-playing",
    };

    private static string BuildAuthUrl(string clientId, string state, string redirectUri)
    {
        var qs = string.Join("&", new Dictionary<string, string>
        {
            ["client_id"]     = clientId,
            ["response_type"] = "code",
            ["redirect_uri"]  = redirectUri,
            ["scope"]         = string.Join(" ", Scopes),
            ["state"]         = state,
        }.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return $"https://accounts.spotify.com/authorize?{qs}";
    }

    private static async Task WriteHtml(HttpListenerResponse res, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        res.ContentType     = "text/html; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        res.Close();
    }

    private static void Redirect(HttpListenerResponse res, string url)
    {
        res.StatusCode          = 302;
        res.Headers["Location"] = url;
        res.Close();
    }

    private static async Task<Dictionary<string, string>> ReadForm(HttpListenerRequest req)
    {
        using var reader = new System.IO.StreamReader(req.InputStream, Encoding.UTF8);
        var body   = await reader.ReadToEndAsync();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in body.Split('&'))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) continue;
            result[Uri.UnescapeDataString(pair[..idx].Replace('+', ' '))] =
                   Uri.UnescapeDataString(pair[(idx + 1)..].Replace('+', ' '));
        }
        return result;
    }

    private static int FindFreePort()
    {
        var sock = new TcpListener(IPAddress.Loopback, 0);
        sock.Start();
        var port = ((IPEndPoint)sock.LocalEndpoint).Port;
        sock.Stop();
        return port;
    }

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private static string? NullIfEmpty(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private const string BaseStyle = @"
<style>
  body{font-family:'Segoe UI',sans-serif;background:#121212;color:#fff;display:flex;
       align-items:center;justify-content:center;min-height:100vh;margin:0}
  .card{background:#1e1e1e;border-radius:12px;padding:36px 40px;width:400px;
        box-shadow:0 8px 32px #0008}
  h1{margin:0 0 6px;font-size:1.4rem}
  .sub{color:#888;font-size:.9rem;margin:0 0 24px}
  .err{color:#e74c3c;font-size:.85rem;margin:0 0 14px}
  label{display:block;font-size:.85rem;color:#aaa;margin-bottom:4px}
  input{width:100%;box-sizing:border-box;padding:10px 12px;border-radius:6px;
        border:1px solid #333;background:#2a2a2a;color:#fff;font-size:.95rem;margin-bottom:16px}
  input:focus{outline:none;border-color:#1db954}
  button{width:100%;padding:11px;background:#1db954;color:#000;font-weight:700;
         border:none;border-radius:6px;cursor:pointer;font-size:1rem}
  button:hover{background:#1ed760}
  .hint{font-size:.8rem;color:#666;margin-top:18px;line-height:1.6}
  a{color:#1db954}
  code{background:#2a2a2a;padding:1px 5px;border-radius:3px;font-size:.78rem}
</style>";

    private static string PageCredentials(string initialId = "", string error = "") => $@"<!DOCTYPE html>
<html lang='en'><head><meta charset='utf-8'><title>WKMusic — Spotify Setup</title>{BaseStyle}</head>
<body><div class='card'>
  <h1>🎵 WKMusic — Spotify Setup</h1>
  <p class='sub'>One-time setup. Token is saved locally.</p>
  {(string.IsNullOrEmpty(error) ? "" : $"<p class='err'>{error}</p>")}
  <form method='POST' action='/submit'>
    <label>Client ID</label>
    <input name='clientId' value='{initialId}' placeholder='32-character hex string' autofocus required>
    <label>Client Secret</label>
    <input name='clientSecret' type='password' placeholder='32-character hex string' required>
    <button type='submit'>Connect Spotify →</button>
  </form>
  <p class='hint'>
    Get your credentials at <a href='https://developer.spotify.com/dashboard' target='_blank'>developer.spotify.com/dashboard</a>.<br>
    Add <code>https://wkmusic.eventeventeventevent1.workers.dev/callback</code> as a Redirect URI.
  </p>
</div></body></html>";

    private static string PageSuccess() => $@"<!DOCTYPE html>
<html lang='en'><head><meta charset='utf-8'><title>WKMusic — Connected</title>{BaseStyle}
<style>.card{{text-align:center}} .icon{{font-size:3rem;margin-bottom:12px}} h1{{color:#1db954}}</style>
</head><body><div class='card'>
  <div class='icon'>🎵</div>
  <h1>Connected!</h1>
  <p class='sub' style='margin:0'>Spotify is linked. You can close this tab and return to the game.</p>
</div></body></html>";

    private static string PageError(string message) => $@"<!DOCTYPE html>
<html lang='en'><head><meta charset='utf-8'><title>WKMusic — Error</title>{BaseStyle}
<style>.card{{text-align:center}} h1{{color:#e74c3c}}</style>
</head><body><div class='card'>
  <h1>⚠ Error</h1>
  <p class='sub' style='margin:0'>{message}</p>
</div></body></html>";
}
