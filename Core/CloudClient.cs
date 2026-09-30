using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSync;

/// <summary>The part of the ClaudeSync Worker API the sync engine needs.</summary>
public interface ICloudApi
{
    Task<JsonNode> Library();
    Task<JsonArray> Versions(string sessionId);
    Task<byte[]> Download(string versionId);
    /// <summary>Publishes a new version. Fails with 409/412 when <paramref name="expectedHead"/> is no longer the head.</summary>
    Task<string> Upload(string sessionId, byte[] package, string deviceId, string expectedHead, string transcriptHash);
}

public sealed class CloudException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public bool IsVersionConflict => Status is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed;
}

public sealed class CloudClient(AppConfig config) : ICloudApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private AuthTokens tokens = LoadTokensSafe();
    public bool HasSession => !string.IsNullOrEmpty(tokens.RefreshToken);

    private static AuthTokens LoadTokensSafe()
    {
        try { return Store.LoadTokens(); }
        catch (Exception ex) { Store.Log("Saved sign-in unreadable: " + ex.Message); return new(); }
    }
    private static JsonNode Parse(string body) => JsonNode.Parse(body) ?? throw new InvalidDataException(L.EmptyResponse);

    // ---- Google sign-in through Supabase (PKCE, loopback redirect) ------------------------------------

    public async Task SignIn(CancellationToken cancel = default)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var verifier = B64(RandomNumberGenerator.GetBytes(64));
        var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect = $"http://127.0.0.1:{port}/callback";
            var url = config.SupabaseUrl.TrimEnd('/') + "/auth/v1/authorize?provider=google&redirect_to=" + Uri.EscapeDataString(redirect)
                + "&code_challenge=" + Uri.EscapeDataString(challenge) + "&code_challenge_method=s256";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                // Browsers open speculative connections and ask for /favicon.ico; only /callback counts.
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                var callback = await ReadCallback(client, timeout.Token);
                if (callback is null) continue;
                if (callback.Value.Code is not { } code)
                    throw new InvalidOperationException(L.SignInNotCompleted + (callback.Value.Error is { } e ? " " + e : ""));
                SetTokens(await AuthPost("pkce", new { auth_code = code, code_verifier = verifier }));
                return;
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException(L.SignInTimeout);
        }
        finally { listener.Stop(); }
    }

    private static async Task<(string? Code, string? Error)?> ReadCallback(TcpClient client, CancellationToken cancel)
    {
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        var stream = client.GetStream();
        var buffer = new byte[16384];
        int count;
        try { count = await stream.ReadAsync(buffer, readTimeout.Token); }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return null; }
        catch (IOException) { return null; }
        if (count == 0) return null;
        var target = Encoding.ASCII.GetString(buffer, 0, count).Split("\r\n")[0].Split(' ').ElementAtOrDefault(1) ?? "";
        if (!target.StartsWith("/callback", StringComparison.Ordinal))
        {
            await Respond(stream, "404 Not Found", "text/plain", "Not found", cancel);
            return null;
        }
        var query = new Uri("http://127.0.0.1" + target).Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.First()[1].Replace('+', ' ')));
        var code = query.GetValueOrDefault("code");
        var error = query.GetValueOrDefault("error_description") ?? query.GetValueOrDefault("error");
        var (title, text) = code is null
            ? (L.BrowserFailedTitle, L.BrowserFailedText)
            : (L.BrowserDoneTitle, L.BrowserDoneText);
        var html = $"<!doctype html><meta charset=utf-8><title>ClaudeSync</title><body style=\"font-family:Segoe UI,sans-serif;display:grid;place-items:center;height:90vh;margin:0;background:#f5f5f7;color:#1b1b1f\"><div style=\"text-align:center\"><div style=\"font-size:44px\">{(code is null ? "&#9888;" : "&#10003;")}</div><h2 style=\"margin:8px 0\">{title}</h2><p style=\"color:#5f6068\">{text}</p></div>";
        await Respond(stream, code is null ? "400 Bad Request" : "200 OK", "text/html", html, cancel);
        return (code, error);
    }

    private static async Task Respond(NetworkStream stream, string status, string type, string body, CancellationToken cancel)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        try { await stream.WriteAsync(header, cancel); await stream.WriteAsync(payload, cancel); }
        catch (IOException) { }
    }

    private async Task<JsonNode> AuthPost(string grant, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, config.SupabaseUrl.TrimEnd('/') + "/auth/v1/token?grant_type=" + grant);
        req.Headers.Add("apikey", config.PublishableKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(req);
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            if (grant == "refresh_token" && (int)response.StatusCode is 400 or 401)
                throw new CloudException(L.SessionExpired, HttpStatusCode.Unauthorized);
            throw new CloudException(L.SignInFailed + content, response.StatusCode);
        }
        return Parse(content);
    }

    private void SetTokens(JsonNode result)
    {
        var expires = result["expires_in"]?.GetValue<int>() ?? 3600;
        tokens = new AuthTokens
        {
            AccessToken = result["access_token"]?.GetValue<string>() ?? "",
            RefreshToken = result["refresh_token"]?.GetValue<string>() ?? tokens.RefreshToken,
            ExpiresIn = expires,
            ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expires - 60
        };
        Store.SaveTokens(tokens);
    }

    private async Task<string> AccessToken(bool forceRefresh = false)
    {
        await tokenGate.WaitAsync();
        try
        {
            if (!forceRefresh && tokens.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() && tokens.AccessToken.Length > 0) return tokens.AccessToken;
            if (tokens.RefreshToken.Length == 0) throw new CloudException(L.SignInFirst, HttpStatusCode.Unauthorized);
            SetTokens(await AuthPost("refresh_token", new { refresh_token = tokens.RefreshToken }));
            return tokens.AccessToken;
        }
        finally { tokenGate.Release(); }
    }

    public void SignOut()
    {
        tokens = new();
        Store.DeleteTokens();
    }

    public async Task<(string Id, string? Email)> User()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, config.SupabaseUrl.TrimEnd('/') + "/auth/v1/user");
        req.Headers.Add("apikey", config.PublishableKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken());
        using var response = await Http.SendAsync(req);
        if (!response.IsSuccessStatusCode) throw new CloudException(L.UserInfoFailed, response.StatusCode);
        var user = Parse(await response.Content.ReadAsStringAsync());
        return (user["id"]?.GetValue<string>() ?? "", user["email"]?.GetValue<string>());
    }

    // ---- Worker API -------------------------------------------------------------------------------

    private async Task<byte[]> Api(HttpMethod method, string path, object? body = null, Dictionary<string, string>? headers = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, config.WorkerUrl.TrimEnd('/') + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken(forceRefresh: attempt > 0));
            req.Headers.UserAgent.ParseAdd("ClaudeSyncDesktop/2.0");
            req.Headers.Accept.ParseAdd("application/json");
            if (headers is not null) foreach (var (key, value) in headers) req.Headers.TryAddWithoutValidation(key, value);
            if (body is byte[] bytes) { req.Content = new ByteArrayContent(bytes); req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip"); }
            else if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try { response = await Http.SendAsync(req); }
            catch (HttpRequestException ex) { throw new CloudException(L.ServerUnreachable + ex.Message, 0); }
            catch (TaskCanceledException) { throw new CloudException(L.ServerTimeout, HttpStatusCode.RequestTimeout); }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
                var payload = await response.Content.ReadAsByteArrayAsync();
                if (response.IsSuccessStatusCode) return payload;
                var detail = Encoding.UTF8.GetString(payload);
                try { detail = Parse(detail)["error"]?.GetValue<string>() ?? detail; } catch (Exception) { }
                throw new CloudException($"{detail} (HTTP {(int)response.StatusCode})", response.StatusCode);
            }
        }
    }
    private async Task<JsonNode> Json(HttpMethod method, string path, object? body = null, Dictionary<string, string>? headers = null)
        => Parse(Encoding.UTF8.GetString(await Api(method, path, body, headers)));

    public Task<JsonNode> Library() => Json(HttpMethod.Get, "/v1/library");
    public async Task<JsonArray> Versions(string sessionId) => (await Json(HttpMethod.Get, "/v1/sessions/" + sessionId + "/versions")).AsArray();
    public Task<byte[]> Download(string versionId) => Api(HttpMethod.Get, "/v1/versions/" + versionId + "/download");
    public async Task<string> Upload(string sessionId, byte[] package, string deviceId, string expectedHead, string transcriptHash)
    {
        var response = await Json(HttpMethod.Put, "/v1/sessions/" + sessionId + "/versions", package, new()
        {
            ["X-Device-Id"] = deviceId, ["X-Expected-Version"] = expectedHead,
            ["X-Transcript-Hash"] = transcriptHash, ["X-Package-Hash"] = Transcript.Hash(package)
        });
        return response["id"]?.ToString() ?? throw new InvalidDataException(L.NoVersionId);
    }
    public async Task<string> RegisterDevice(string name) => (await Json(HttpMethod.Post, "/v1/devices", new { name }))["id"]?.ToString() ?? "";
    public async Task<string> CreateProject(string name) => (await Json(HttpMethod.Post, "/v1/projects", new { name }))["id"]?.ToString() ?? "";
    public async Task<string> CreateSession(string projectId, string cliSessionId, string desktopSessionId, string title)
        => (await Json(HttpMethod.Post, "/v1/sessions", new { projectId, cliSessionId, desktopSessionId, title }))["id"]?.ToString() ?? "";
    public Task Link(string deviceId, string sessionId) => Json(HttpMethod.Post, "/v1/links", new { deviceId, sessionId });

    /// <summary>Deletes the conversation and all its versions from the cloud (not from any computer).</summary>
    public async Task DeleteSession(string sessionId)
    {
        try { await Api(HttpMethod.Delete, "/v1/sessions/" + sessionId); }
        catch (CloudException ex) when (ex.Status == HttpStatusCode.NotFound && ex.Message.StartsWith("Not found", StringComparison.Ordinal))
        {
            throw new CloudException(L.ServerNoDelete, ex.Status);
        }
        catch (CloudException ex) when (ex.Status == HttpStatusCode.NotFound) { } // already gone
    }
}
