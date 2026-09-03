using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Client for Webex's KMS (the end-to-end encryption key service), used to
// decrypt reaction names and to encrypt the reactions this app posts. The
// public REST API has no reactions, and on the internal conversation service
// their names are E2E-encrypted with a per-space key that only KMS hands out.
//
// Protocol (verified live against kms-us.wbx2.com; mirrors Cisco's node-kms):
//   1. Register a device (WDM) — KMS responses only arrive over the device's
//      Mercury websocket, never in the HTTP response.
//   2. GET /kms/{userId} for the KMS cluster + its static RSA public key.
//   3. ECDHE handshake: send our P-256 public key wrapped as a JWE to the RSA
//      key; the response (a JWS) carries the server's P-256 key. Shared
//      secret -> HKDF-SHA256 -> a 256-bit "ephemeral key" for the session.
//   4. Key fetches are JWEs (dir/A256GCM) under that ephemeral key.
//
// Requires the spark:kms OAuth scope on top of spark:all — without it the KMS
// answers 403 ("allowed: []") and reactions stay sealed; callers treat a null
// key as "can't decrypt" and degrade gracefully.
static class WebexKms
{
    const string WdmUrl = "https://wdm-a.wbx2.com/wdm/api/v1/devices";
    const string EncryptionBase = "https://encryption-a.wbx2.com/encryption/api/v1";

    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly SemaphoreSlim Gate = new(1, 1); // one KMS request in flight at a time

    // Session state, built lazily on the first key request.
    static string? _deviceUrl;
    static string? _wsUrl;
    static string? _userId;
    static ClientWebSocket? _ws;
    static string? _kmsCluster;
    static byte[]? _ephemeralKey;
    static string? _ephemeralUri;
    static DateTimeOffset _ephemeralExpires;

    // One space key, with the raw JWK strings kept for the reaction hmac.
    public sealed record KmsKey(string Uri, string K, string Kid, string Kty)
    {
        public byte[] Bytes => UnB64Url(K);
    }

    static readonly Dictionary<string, KmsKey?> KeyCache = []; // keyUrl -> key (null = fetch failed)

    // ----- public surface ----------------------------------------------------

    // The space key behind a kms:// key URL, or null when it can't be had
    // (most commonly: the token lacks spark:kms). Failures are cached for the
    // session so a sealed space doesn't retry on every render.
    public static async Task<KmsKey?> TryGetKeyAsync(string keyUrl)
    {
        lock (KeyCache)
            if (KeyCache.TryGetValue(keyUrl, out var cached)) return cached;

        await Gate.WaitAsync();
        try
        {
            lock (KeyCache)
                if (KeyCache.TryGetValue(keyUrl, out var cached)) return cached;

            KmsKey? key = null;
            try
            {
                key = await FetchKeyAsync(keyUrl);
            }
            catch (Exception ex)
            {
                AppLog.Debug($"webex kms key {keyUrl}", ex);
            }
            lock (KeyCache) KeyCache[keyUrl] = key;
            return key;
        }
        finally
        {
            Gate.Release();
        }
    }

    // Decrypts a compact JWE (dir/A256GCM) produced by a Webex client, e.g. a
    // reaction displayName. Null when the text isn't a JWE or doesn't open.
    public static string? TryDecrypt(KmsKey key, string compactJwe)
    {
        try
        {
            var parts = compactJwe.Split('.');
            if (parts.Length != 5) return null;
            return Encoding.UTF8.GetString(JweDecrypt(key.Bytes, parts));
        }
        catch (Exception ex)
        {
            AppLog.Debug("webex kms decrypt", ex);
            return null;
        }
    }

    // Encrypts text the way Webex clients do (compact JWE, dir + A256GCM).
    public static string Encrypt(KmsKey key, string plaintext)
    {
        // Header matches the clients' byte-for-byte ({"enc":...,"alg":"dir"}).
        var header = B64Url(Encoding.UTF8.GetBytes("{\"enc\":\"A256GCM\",\"alg\":\"dir\"}"));
        var iv = RandomNumberGenerator.GetBytes(12);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key.Bytes, 16);
        gcm.Encrypt(iv, pt, ct, tag, Encoding.ASCII.GetBytes(header));
        return $"{header}..{B64Url(iv)}.{B64Url(ct)}.{B64Url(tag)}";
    }

    // The hmac real clients attach to a reaction: HMAC-SHA256 keyed on the
    // parent activity id over {"k","kid","kty"} of the space key + parent id +
    // the plaintext reaction name (crypto-js hmacSHA256(source, parent.id)).
    public static string ReactionHmac(KmsKey key, string parentActivityId, string displayName)
    {
        var jwk = $"{{\"k\":\"{key.K}\",\"kid\":\"{key.Kid}\",\"kty\":\"{key.Kty}\"}}";
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(parentActivityId));
        return Convert.ToHexString(
            h.ComputeHash(Encoding.UTF8.GetBytes(jwk + parentActivityId + displayName))).ToLowerInvariant();
    }

    // Removes this session's temporary device registration. Best-effort, for
    // app shutdown; everything else times out server-side on its own.
    public static async Task ShutdownAsync()
    {
        try { _ws?.Dispose(); } catch { /* closing anyway */ }
        _ws = null;
        if (_deviceUrl == null) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, _deviceUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + await WebexApi.GetTokenAsync());
            await Client.SendAsync(req);
        }
        catch (Exception ex) { AppLog.Debug("webex kms device delete", ex); }
        _deviceUrl = null;
    }

    // ----- KMS plumbing ------------------------------------------------------

    static async Task<KmsKey?> FetchKeyAsync(string keyUrl)
    {
        await EnsureContextAsync();
        var body = JsonSerializer.Serialize(new
        {
            client = ClientInfo(await WebexApi.GetTokenAsync()),
            method = "retrieve",
            uri = keyUrl,
            requestId = Guid.NewGuid().ToString(),
        });
        using var doc = await RequestAsync(body, useEphemeralKey: true);
        var root = doc.RootElement;
        if (root.TryGetProperty("status", out var st) && st.GetInt32() >= 400)
        {
            AppLog.Debug($"webex kms: {root.GetRawText()}");
            return null; // most likely 403 — token granted without spark:kms
        }
        var jwk = root.GetProperty("key").GetProperty("jwk");
        return new KmsKey(
            root.GetProperty("key").GetProperty("uri").GetString()!,
            jwk.GetProperty("k").GetString()!,
            jwk.TryGetProperty("kid", out var kid) && kid.ValueKind == JsonValueKind.String ? kid.GetString()! : keyUrl,
            jwk.TryGetProperty("kty", out var kty) && kty.ValueKind == JsonValueKind.String ? kty.GetString()! : "oct");
    }

    static object ClientInfo(string bearer) =>
        new { clientId = _deviceUrl, credential = new { userId = _userId, bearer } };

    // Device + websocket + ECDHE session key, built once and rebuilt when the
    // ephemeral key ages out (KMS issues them for about an hour).
    static async Task EnsureContextAsync()
    {
        var token = await WebexApi.GetTokenAsync();

        if (_deviceUrl == null)
        {
            var payload = JsonSerializer.Serialize(new
            {
                deviceType = "DESKTOP",
                name = "dailyshell",
                model = "DailyShell",
                localizedModel = "DailyShell",
                systemName = "Windows",
                systemVersion = "11",
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, WdmUrl)
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            var resp = await Client.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            _deviceUrl = doc.RootElement.GetProperty("url").GetString();
            _wsUrl = doc.RootElement.GetProperty("webSocketUrl").GetString();
            _userId = doc.RootElement.GetProperty("userId").GetString();
        }

        await EnsureMercuryAsync(token);

        if (_ephemeralKey != null && DateTimeOffset.UtcNow < _ephemeralExpires - TimeSpan.FromMinutes(5)) return;

        if (_kmsCluster == null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{EncryptionBase}/kms/{_userId}");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            var resp = await Client.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            _kmsCluster = doc.RootElement.GetProperty("kmsCluster").GetString();
            using var rsaDoc = JsonDocument.Parse(doc.RootElement.GetProperty("rsaPublicKey").GetString()!);
            _rsaKid = rsaDoc.RootElement.GetProperty("kid").GetString()!;
            _rsaN = rsaDoc.RootElement.GetProperty("n").GetString()!;
            _rsaE = rsaDoc.RootElement.GetProperty("e").GetString()!;
        }

        // ECDHE: our fresh P-256 public key goes up wrapped to the KMS's RSA
        // key; the signed response carries the server half to derive from.
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var pub = ecdh.PublicKey.ExportParameters();
        var body = JsonSerializer.Serialize(new
        {
            client = ClientInfo(await WebexApi.GetTokenAsync()),
            method = "create",
            uri = $"{_kmsCluster}/ecdhe",
            jwk = new { kty = "EC", crv = "P-256", x = B64Url(pub.Q.X!), y = B64Url(pub.Q.Y!) },
            requestId = Guid.NewGuid().ToString(),
        });
        using var doc2 = await RequestAsync(body, useEphemeralKey: false);
        var key = doc2.RootElement.GetProperty("key");
        var jwk2 = key.GetProperty("jwk");
        using var serverEcdh = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = UnB64Url(jwk2.GetProperty("x").GetString()!),
                Y = UnB64Url(jwk2.GetProperty("y").GetString()!),
            },
        });
        var z = ecdh.DeriveRawSecretAgreement(serverEcdh.PublicKey);
        _ephemeralKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, z, 32, new byte[32], []);
        _ephemeralUri = key.GetProperty("uri").GetString();
        _ephemeralExpires = key.TryGetProperty("expirationDate", out var exp) && exp.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(exp.GetString()!)
            : DateTimeOffset.UtcNow.AddMinutes(50);
    }

    static string? _rsaKid, _rsaN, _rsaE;

    static async Task EnsureMercuryAsync(string token)
    {
        if (_ws is { State: WebSocketState.Open }) return;
        try { _ws?.Dispose(); } catch { /* replacing it */ }
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(_wsUrl!), CancellationToken.None);
        var auth = JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString(),
            type = "authorization",
            data = new { token = "Bearer " + token },
        });
        await _ws.SendAsync(Encoding.UTF8.GetBytes(auth), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    // Sends one wrapped KMS request and pumps the websocket (acking every
    // Mercury event on the way) until its answer arrives. The single-flight
    // gate in TryGetKeyAsync means any kms_message we see is for us.
    static async Task<JsonDocument> RequestAsync(string bodyJson, bool useEphemeralKey)
    {
        var wrapped = useEphemeralKey
            ? JweEncryptDir(_ephemeralKey!, _ephemeralUri!, bodyJson)
            : JweEncryptRsa(bodyJson);

        var post = JsonSerializer.Serialize(new { destination = _kmsCluster, kmsMessages = new[] { wrapped } });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{EncryptionBase}/kms/messages")
        { Content = new StringContent(post, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + await WebexApi.GetTokenAsync());
        var resp = await Client.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var buffer = new byte[512 * 1024];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(deadline - DateTime.UtcNow);
            var sb = new StringBuilder();
            WebSocketReceiveResult r;
            do
            {
                r = await _ws!.ReceiveAsync(buffer, cts.Token);
                sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
            } while (!r.EndOfMessage);
            if (sb.Length == 0) continue;

            using var frame = JsonDocument.Parse(sb.ToString());
            var root = frame.RootElement;
            if (root.TryGetProperty("id", out var mid) && mid.ValueKind == JsonValueKind.String)
            {
                var ack = JsonSerializer.Serialize(new { type = "ack", messageId = mid.GetString() });
                await _ws.SendAsync(Encoding.UTF8.GetBytes(ack), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("eventType", out var et) ||
                et.GetString() != "encryption.kms_message") continue;

            foreach (var m in data.GetProperty("encryption").GetProperty("kmsMessages").EnumerateArray())
            {
                var parts = m.GetString()!.Split('.');
                // ECDHE answers come back signed (JWS, 3 parts, payload in the
                // middle); everything else is a JWE under the ephemeral key.
                var payload = parts.Length switch
                {
                    3 => UnB64Url(parts[1]),
                    5 => JweDecrypt(_ephemeralKey!, parts),
                    _ => null,
                };
                if (payload != null) return JsonDocument.Parse(payload);
            }
        }
        throw new TimeoutException("The Webex key service didn't answer in time.");
    }

    // ----- JOSE helpers ------------------------------------------------------

    static string JweEncryptRsa(string bodyJson)
    {
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = UnB64Url(_rsaN!), Exponent = UnB64Url(_rsaE!) });
        var cek = RandomNumberGenerator.GetBytes(32);
        var header = B64Url(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { alg = "RSA-OAEP", enc = "A256GCM", kid = _rsaKid })));
        var iv = RandomNumberGenerator.GetBytes(12);
        var pt = Encoding.UTF8.GetBytes(bodyJson);
        var ct = new byte[pt.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(cek, 16);
        gcm.Encrypt(iv, pt, ct, tag, Encoding.ASCII.GetBytes(header));
        return $"{header}.{B64Url(rsa.Encrypt(cek, RSAEncryptionPadding.OaepSHA1))}.{B64Url(iv)}.{B64Url(ct)}.{B64Url(tag)}";
    }

    static string JweEncryptDir(byte[] key, string kid, string bodyJson)
    {
        var header = B64Url(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { alg = "dir", enc = "A256GCM", kid })));
        var iv = RandomNumberGenerator.GetBytes(12);
        var pt = Encoding.UTF8.GetBytes(bodyJson);
        var ct = new byte[pt.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(iv, pt, ct, tag, Encoding.ASCII.GetBytes(header));
        return $"{header}..{B64Url(iv)}.{B64Url(ct)}.{B64Url(tag)}";
    }

    static byte[] JweDecrypt(byte[] key, string[] parts)
    {
        var iv = UnB64Url(parts[2]);
        var ct = UnB64Url(parts[3]);
        var tag = UnB64Url(parts[4]);
        var pt = new byte[ct.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(iv, ct, tag, pt, Encoding.ASCII.GetBytes(parts[0]));
        return pt;
    }

    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[] UnB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" });
    }
}
