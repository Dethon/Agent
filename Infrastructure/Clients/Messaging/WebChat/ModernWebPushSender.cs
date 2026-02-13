using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Infrastructure.Clients.Messaging.WebChat;

/// <summary>
/// Web Push sender using aes128gcm content encoding (RFC 8188/8291) and
/// vapid Authorization header (RFC 8292).
/// Required for WNS (Edge/Windows) which rejects the legacy aesgcm + "WebPush" auth.
/// </summary>
public sealed class ModernWebPushSender(string publicKey, string privateKey, string subject)
    : IPushMessageSender, IDisposable
{
    private readonly HttpClient _httpClient = new();

    public async Task SendAsync(string endpoint, string p256dh, string auth, string payload,
        CancellationToken ct = default)
    {
        var request = BuildRequest(endpoint, p256dh, auth, payload);
        var response = await _httpClient.SendAsync(request, ct);
        await HandleResponse(response, endpoint);
    }

    private HttpRequestMessage BuildRequest(string endpoint, string p256dh, string auth, string payload)
    {
        var uri = new Uri(endpoint);
        var audience = $"{uri.Scheme}://{uri.Host}";

        var jwt = CreateVapidJwt(audience);
        var encryptedPayload = EncryptPayload(p256dh, auth, Encoding.UTF8.GetBytes(payload));

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("TTL", "2419200");
        request.Headers.Add("Urgency", "normal");
        request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={jwt},k={publicKey}");

        request.Content = new ByteArrayContent(encryptedPayload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Content.Headers.ContentLength = encryptedPayload.Length;

        return request;
    }

    private string CreateVapidJwt(string audience)
    {
        var publicKeyBytes = Base64UrlDecode(publicKey);
        var privateKeyBytes = Base64UrlDecode(privateKey);

        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKeyBytes,
            Q = new ECPoint
            {
                X = publicKeyBytes[1..33],
                Y = publicKeyBytes[33..65]
            }
        });

        var header = Base64UrlEncode("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"u8);
        var exp = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds();
        var claims = Encoding.UTF8.GetBytes(
            $"{{\"aud\":\"{audience}\",\"exp\":{exp},\"sub\":\"{subject}\"}}");
        var claimsB64 = Base64UrlEncode(claims);

        var unsignedToken = $"{header}.{claimsB64}";
        var signature = ecdsa.SignData(
            Encoding.ASCII.GetBytes(unsignedToken), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"{unsignedToken}.{Base64UrlEncode(signature)}";
    }

    internal static byte[] EncryptPayload(string p256dh, string authKey, byte[] plaintext)
    {
        var subscriberPub = Base64UrlDecode(p256dh);
        var authBytes = Base64UrlDecode(authKey);

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralPub = ExportUncompressedPublicKey(ephemeral);

        using var subscriberEcdh = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = subscriberPub[1..33], Y = subscriberPub[33..65] }
        });

        var sharedSecret = ephemeral.DeriveRawSecretAgreement(subscriberEcdh.PublicKey);
        var salt = RandomNumberGenerator.GetBytes(16);

        // IKM: HKDF(salt=auth, ikm=sharedSecret, info="WebPush: info\0" || uaPub || asPub)
        var keyInfo = BuildKeyInfo(subscriberPub, ephemeralPub);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, authBytes, keyInfo);

        // CEK and nonce from IKM + salt
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt,
            "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt,
            "Content-Encoding: nonce\0"u8.ToArray());

        // Pad: plaintext || 0x02 (last-record delimiter per RFC 8188)
        var padded = new byte[plaintext.Length + 1];
        plaintext.CopyTo(padded, 0);
        padded[^1] = 0x02;

        // AES-128-GCM
        var ciphertext = new byte[padded.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(cek, 16);
        aes.Encrypt(nonce, padded, ciphertext, tag);

        // aes128gcm payload: salt(16) | rs(4 BE) | idlen(1) | keyid(65) | ciphertext | tag
        var result = new byte[16 + 4 + 1 + 65 + ciphertext.Length + tag.Length];
        var pos = 0;

        salt.CopyTo(result, pos); pos += 16;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(pos), 4096); pos += 4; // rs = 4096
        result[pos++] = 65;
        ephemeralPub.CopyTo(result, pos); pos += 65;
        ciphertext.CopyTo(result, pos); pos += ciphertext.Length;
        tag.CopyTo(result, pos);

        return result;
    }

    internal static byte[] BuildKeyInfo(byte[] subscriberPub, byte[] ephemeralPub)
    {
        // "WebPush: info\0" || subscriberPublicKey(65) || ephemeralPublicKey(65)
        var label = "WebPush: info\0"u8;
        var info = new byte[label.Length + 65 + 65];
        label.CopyTo(info);
        subscriberPub.CopyTo(info.AsSpan(label.Length));
        ephemeralPub.CopyTo(info.AsSpan(label.Length + 65));
        return info;
    }

    private static byte[] ExportUncompressedPublicKey(ECDiffieHellman key)
    {
        var q = key.ExportParameters(false).Q;
        var result = new byte[65];
        result[0] = 0x04;
        q.X!.CopyTo(result, 1);
        q.Y!.CopyTo(result, 33);
        return result;
    }

    private static async Task HandleResponse(HttpResponseMessage response, string endpoint)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        var message = $"Push to {endpoint} failed: {(int)response.StatusCode} {response.ReasonPhrase}";
        if (!string.IsNullOrEmpty(body))
        {
            message += $". {body}";
        }

        throw new WebPushSendException(message, response.StatusCode);
    }

    internal static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }

    internal static string Base64UrlEncode(ReadOnlySpan<byte> input)
        => Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _httpClient.Dispose();
}
