using System.Security.Cryptography;
using System.Text;
using Infrastructure.Clients.Messaging.WebChat;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public sealed class ModernWebPushEncryptionTests
{
    [Fact]
    public void EncryptPayload_RoundTrip_DecryptsToOriginalPlaintext()
    {
        // Arrange: generate a subscriber keypair (simulating what the browser does)
        using var subscriberKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var subscriberParams = subscriberKey.ExportParameters(true);
        var subscriberPub = new byte[65];
        subscriberPub[0] = 0x04;
        subscriberParams.Q.X!.CopyTo(subscriberPub, 1);
        subscriberParams.Q.Y!.CopyTo(subscriberPub, 33);

        var authSecret = RandomNumberGenerator.GetBytes(16);

        var p256dh = ModernWebPushSender.Base64UrlEncode(subscriberPub);
        var auth = ModernWebPushSender.Base64UrlEncode(authSecret);
        var plaintext = Encoding.UTF8.GetBytes("{\"title\":\"Test\",\"body\":\"Hello World\"}");

        // Act: encrypt using our implementation
        var payload = ModernWebPushSender.EncryptPayload(p256dh, auth, plaintext);

        // Assert: decrypt and verify (simulating what the browser does)
        var decrypted = DecryptAes128Gcm(payload, subscriberKey, subscriberPub, authSecret);
        decrypted.ShouldBe(plaintext);
    }

    [Fact]
    public void EncryptPayload_PayloadHasCorrectStructure()
    {
        using var subscriberKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var subscriberParams = subscriberKey.ExportParameters(true);
        var subscriberPub = new byte[65];
        subscriberPub[0] = 0x04;
        subscriberParams.Q.X!.CopyTo(subscriberPub, 1);
        subscriberParams.Q.Y!.CopyTo(subscriberPub, 33);

        var authSecret = RandomNumberGenerator.GetBytes(16);
        var p256dh = ModernWebPushSender.Base64UrlEncode(subscriberPub);
        var auth = ModernWebPushSender.Base64UrlEncode(authSecret);
        var plaintext = Encoding.UTF8.GetBytes("test");

        var payload = ModernWebPushSender.EncryptPayload(p256dh, auth, plaintext);

        // Header: salt(16) + rs(4) + idlen(1) + keyid(65) = 86 bytes
        payload.Length.ShouldBeGreaterThan(86);

        // rs should be 4096 (0x00001000 big-endian)
        payload[16].ShouldBe((byte)0x00);
        payload[17].ShouldBe((byte)0x00);
        payload[18].ShouldBe((byte)0x10);
        payload[19].ShouldBe((byte)0x00);

        // idlen should be 65
        payload[20].ShouldBe((byte)65);

        // keyid should start with 0x04 (uncompressed point)
        payload[21].ShouldBe((byte)0x04);

        // Total: 86 header + plaintext.Length + 1 (padding) + 16 (tag) = 86 + 5 + 16 = 107
        payload.Length.ShouldBe(86 + plaintext.Length + 1 + 16);
    }

    [Fact]
    public void EncryptPayload_DifferentCallsProduceDifferentOutputs()
    {
        using var subscriberKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var subscriberParams = subscriberKey.ExportParameters(true);
        var subscriberPub = new byte[65];
        subscriberPub[0] = 0x04;
        subscriberParams.Q.X!.CopyTo(subscriberPub, 1);
        subscriberParams.Q.Y!.CopyTo(subscriberPub, 33);

        var authSecret = RandomNumberGenerator.GetBytes(16);
        var p256dh = ModernWebPushSender.Base64UrlEncode(subscriberPub);
        var auth = ModernWebPushSender.Base64UrlEncode(authSecret);
        var plaintext = Encoding.UTF8.GetBytes("same message");

        var payload1 = ModernWebPushSender.EncryptPayload(p256dh, auth, plaintext);
        var payload2 = ModernWebPushSender.EncryptPayload(p256dh, auth, plaintext);

        // Different salt and ephemeral key each time
        Convert.ToHexString(payload1).ShouldNotBe(Convert.ToHexString(payload2));

        // But both should decrypt to the same plaintext
        var decrypted1 = DecryptAes128Gcm(payload1, subscriberKey, subscriberPub, authSecret);
        var decrypted2 = DecryptAes128Gcm(payload2, subscriberKey, subscriberPub, authSecret);
        decrypted1.ShouldBe(plaintext);
        decrypted2.ShouldBe(plaintext);
    }
    
    private static byte[] DecryptAes128Gcm(
        byte[] payload, ECDiffieHellman subscriberPrivateKey, byte[] subscriberPub, byte[] authSecret)
    {
        // Parse header: salt(16) | rs(4) | idlen(1) | keyid(idlen)
        var salt = payload[..16];
        // rs = payload[16..20] big-endian (we don't need it for single-record)
        var idLen = payload[20];
        var ephemeralPub = payload[21..(21 + idLen)];
        var ciphertextAndTag = payload[(21 + idLen)..];

        // ECDH: subscriber private key + ephemeral public key
        using var ephemeralEcdh = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = ephemeralPub[1..33], Y = ephemeralPub[33..65] }
        });

        var sharedSecret = subscriberPrivateKey.DeriveRawSecretAgreement(ephemeralEcdh.PublicKey);

        // IKM: HKDF(salt=auth, ikm=sharedSecret, info="WebPush: info\0" || subscriberPub || ephemeralPub)
        var keyInfo = ModernWebPushSender.BuildKeyInfo(subscriberPub, ephemeralPub);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, authSecret, keyInfo);

        // CEK and nonce
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt,
            "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt,
            "Content-Encoding: nonce\0"u8.ToArray());

        // Split ciphertext and tag
        var ciphertext = ciphertextAndTag[..^16];
        var tag = ciphertextAndTag[^16..];

        // Decrypt
        var padded = new byte[ciphertext.Length];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, ciphertext, tag, padded);

        // Remove padding: find the last non-zero byte (should be 0x02 for final record)
        var lastNonZero = padded.Length - 1;
        while (lastNonZero >= 0 && padded[lastNonZero] == 0)
        {
            lastNonZero--;
        }

        padded[lastNonZero].ShouldBe((byte)0x02, "Final record delimiter should be 0x02");

        return padded[..lastNonZero];
    }
}
