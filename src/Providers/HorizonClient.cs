using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZweDesk.Core;

namespace ZweDesk.Providers;

/// <summary>
/// Minimal client for the Horizon Connection Server REST API (Horizon 8 / 2006 and later):
/// login, desktop pools, pool entitlements, AD user lookup, user entitlement.
/// Endpoint versions are probed from the newest known down to v1 so the client works across Horizon releases.
/// </summary>
public sealed class HorizonClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly string _base;
    private string? _refreshToken;

    public HorizonClient(string url, bool ignoreCertificateErrors)
    {
        var u = url.Trim().TrimEnd('/');
        if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) u = "https://" + u;
        if (!u.EndsWith("/rest", StringComparison.OrdinalIgnoreCase)) u += "/rest";
        _base = u;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, errors) => ignoreCertificateErrors || errors == SslPolicyErrors.None,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string BaseUrl => _base;

    // ------------------------------------------------------------------ DTOs (Horizon field names)

    public sealed class PoolDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public bool Enabled { get; set; } = true;
        public string? Type { get; set; }
        public string? Source { get; set; }
    }

    public sealed class EntitlementDto
    {
        public string Id { get; set; } = "";
        [JsonPropertyName("ad_user_or_group_ids")] public List<string> AdUserOrGroupIds { get; set; } = new();
    }

    public sealed class AdObjectDto
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
        public string? Domain { get; set; }
        public string? Sid { get; set; }
        public bool Group { get; set; }
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
        [JsonPropertyName("distinguished_name")] public string? DistinguishedName { get; set; }
    }

    private sealed class LoginResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    }

    private sealed class ItemResult
    {
        public string? Id { get; set; }
        [JsonPropertyName("status_code")] public int StatusCode { get; set; }
        public List<ItemError>? Errors { get; set; }
    }

    private sealed class ItemError
    {
        [JsonPropertyName("error_key")] public string? Key { get; set; }
        [JsonPropertyName("error_message")] public string? Message { get; set; }
    }

    // ------------------------------------------------------------------ session

    public async Task LoginAsync(string domain, string user, string password, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsJsonAsync(_base + "/login", new { domain, username = user, password }, Json, ct);
        }
        catch (HttpRequestException ex)
        {
            var hint = ex.InnerException is System.Security.Authentication.AuthenticationException || (ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase))
                ? " The server certificate is not trusted — install the CA certificate or enable \"ignore certificate errors\" for this environment."
                : "";
            throw new AppException($"Cannot reach Horizon at {_base}: {ex.Message}.{hint}", ex.ToString(), ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AppException($"Horizon at {_base} did not answer within 40 s.", ex.ToString(), ex);
        }
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new AppException($"Horizon login failed (HTTP {(int)resp.StatusCode}): {Short(body)}", body);
        var login = JsonSerializer.Deserialize<LoginResponse>(body, Json);
        if (string.IsNullOrEmpty(login?.AccessToken))
            throw new AppException("Horizon login returned no access token.", body);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        _refreshToken = login.RefreshToken;
    }

    public async Task LogoutAsync()
    {
        try
        {
            if (_refreshToken is not null)
                await _http.PostAsJsonAsync(_base + "/logout", new { refresh_token = _refreshToken }, Json, CancellationToken.None);
        }
        catch { /* best effort */ }
    }

    // ------------------------------------------------------------------ queries

    private async Task<T> GetVersionedAsync<T>(string template, CancellationToken ct, int newest = 7)
    {
        string? lastError = null;
        for (var v = newest; v >= 1; v--)
        {
            var path = template.Replace("{v}", "v" + v);
            using var resp = await _http.GetAsync(_base + path, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                try { return JsonSerializer.Deserialize<T>(body, Json) ?? throw new AppException($"Horizon GET {path} returned empty JSON", body); }
                catch (JsonException ex) { throw new AppException($"Horizon GET {path} returned unexpected JSON: {ex.Message}", body, ex); }
            }
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.MethodNotAllowed or HttpStatusCode.BadRequest)
            {
                lastError = $"HTTP {(int)resp.StatusCode} for {path}: {Short(body)}";
                continue;
            }
            throw new AppException($"Horizon GET {path} failed (HTTP {(int)resp.StatusCode}): {Short(body)}", body);
        }
        throw new AppException("No supported Horizon API version answered " + template.Replace("{v}", "v*") + ". " + lastError);
    }

    public Task<List<PoolDto>> PoolsAsync(CancellationToken ct) =>
        GetVersionedAsync<List<PoolDto>>("/inventory/{v}/desktop-pools", ct);

    public Task<List<EntitlementDto>> PoolEntitlementsAsync(CancellationToken ct) =>
        GetVersionedAsync<List<EntitlementDto>>("/entitlements/{v}/desktop-pools", ct, 2);

    public async Task<AdObjectDto?> AdObjectAsync(string id, CancellationToken ct)
    {
        try { return await GetVersionedAsync<AdObjectDto>("/external/{v}/ad-users-or-groups/" + Uri.EscapeDataString(id), ct, 3); }
        catch (AppException) { return null; }
    }

    public async Task<AdObjectDto?> FindAdUserAsync(string? sid, string samAccountName, string? domain, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(sid))
        {
            var bySid = await GetVersionedAsync<List<AdObjectDto>>("/external/{v}/ad-users-or-groups?filter=" + Filter(new { type = "Equals", name = "sid", value = sid }), ct, 3);
            var hit = bySid.FirstOrDefault(o => !o.Group);
            if (hit is not null) return hit;
        }
        object filter = string.IsNullOrEmpty(domain)
            ? new { type = "Equals", name = "name", value = samAccountName }
            : new { type = "And", filters = new object[] { new { type = "Equals", name = "name", value = samAccountName }, new { type = "Equals", name = "domain", value = domain } } };
        var byName = await GetVersionedAsync<List<AdObjectDto>>("/external/{v}/ad-users-or-groups?filter=" + Filter(filter), ct, 3);
        return byName.FirstOrDefault(o => !o.Group && string.Equals(o.Name, samAccountName, StringComparison.OrdinalIgnoreCase)) ?? byName.FirstOrDefault(o => !o.Group);
    }

    private static string Filter(object f) => Uri.EscapeDataString(JsonSerializer.Serialize(f, Json));

    /// <summary>Entitles one AD user or group id to a desktop pool.</summary>
    public async Task EntitleAsync(string poolId, string adUserOrGroupId, CancellationToken ct)
    {
        var payload = new[] { new { id = poolId, ad_user_or_group_ids = new[] { adUserOrGroupId } } };
        string? lastError = null;
        for (var v = 2; v >= 1; v--)
        {
            var path = $"/entitlements/v{v}/desktop-pools";
            using var resp = await _http.PostAsJsonAsync(_base + path, payload, Json, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.MethodNotAllowed)
            {
                lastError = $"HTTP {(int)resp.StatusCode} for {path}";
                continue;
            }
            if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 207)
                throw new AppException($"Horizon entitlement failed (HTTP {(int)resp.StatusCode}): {Short(body)}", body);
            List<ItemResult>? items = null;
            try { items = JsonSerializer.Deserialize<List<ItemResult>>(body, Json); } catch { }
            var bad = items?.FirstOrDefault(i => i.StatusCode is not (200 or 201 or 204));
            if (bad is not null)
            {
                var msg = bad.Errors is { Count: > 0 } ? string.Join("; ", bad.Errors.Select(e => e.Message ?? e.Key)) : "status " + bad.StatusCode;
                if (msg.Contains("already", StringComparison.OrdinalIgnoreCase) || msg.Contains("exists", StringComparison.OrdinalIgnoreCase)) return;
                throw new AppException("Horizon refused the entitlement: " + msg, body);
            }
            return;
        }
        throw new AppException("No supported Horizon entitlement API version answered. " + lastError);
    }

    private static string Short(string body) => body.Length <= 300 ? body : body[..300] + "…";

    public void Dispose() => _http.Dispose();
}
