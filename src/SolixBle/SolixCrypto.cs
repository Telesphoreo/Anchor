using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SolixBle;

/// <summary>Ciphers and key agreement of the negotiation (port of SolixBLE <c>utilities.py</c>).</summary>
internal static class SolixCrypto
{
    private const int GcmTagSize = 16;

    /// <summary>AES-CBC encrypt a payload with PKCS7 padding.</summary>
    public static byte[] CbcEncrypt(byte[] key, byte[] iv, byte[] payload)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(payload, iv, PaddingMode.PKCS7);
    }

    /// <summary>AES-CBC decrypt a payload and strip its PKCS7 padding.</summary>
    public static byte[] CbcDecrypt(byte[] key, byte[] iv, byte[] payload)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(payload, iv, PaddingMode.PKCS7);
    }

    /// <summary>AES-GCM encrypt a payload, appending the 16-byte tag.</summary>
    public static byte[] GcmEncrypt(byte[] key, byte[] nonce, byte[] aad, byte[] payload)
    {
        var output = new byte[payload.Length + GcmTagSize];
        using var gcm = new AesGcm(key, GcmTagSize);
        gcm.Encrypt(nonce, payload, output.AsSpan(0, payload.Length), output.AsSpan(payload.Length), aad);
        return output;
    }

    /// <summary>
    /// AES-GCM decrypt a payload whose last 16 bytes are the tag. If the tag
    /// does not verify the payload is decrypted anyway and the failure is
    /// logged, so a corrupted frame is visible rather than silently dropped.
    /// </summary>
    public static byte[] GcmDecrypt(byte[] key, byte[] nonce, byte[] aad, byte[] payload)
    {
        if (payload.Length < GcmTagSize)
            throw new FormatException($"GCM payload is too short ({payload.Length} bytes)!");

        var encrypted = payload.AsSpan(0, payload.Length - GcmTagSize);
        var tag = payload.AsSpan(payload.Length - GcmTagSize);
        var plaintext = new byte[encrypted.Length];
        try
        {
            using var gcm = new AesGcm(key, GcmTagSize);
            gcm.Decrypt(nonce, encrypted, tag, plaintext, aad);
            return plaintext;
        }
        catch (AuthenticationTagMismatchException)
        {
            SolixLog.Error("Failed to validate authenticity of payload, decoding anyway...");
            return GcmKeystreamDecrypt(key, nonce, encrypted);
        }
    }

    /// <summary>
    /// GCM decryption without the tag check: AES-CTR from counter block
    /// nonce || 00000002 (the block after J0 for a 12-byte nonce).
    /// </summary>
    private static byte[] GcmKeystreamDecrypt(byte[] key, byte[] nonce, ReadOnlySpan<byte> encrypted)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var counter = new byte[16];
        nonce.CopyTo(counter, 0);
        var plaintext = new byte[encrypted.Length];
        var block = new byte[16];
        for (var offset = 0; offset < encrypted.Length; offset += 16)
        {
            BinaryPrimitives.WriteUInt32BigEndian(counter.AsSpan(12), (uint)(offset / 16 + 2));
            aes.EncryptEcb(counter, block, PaddingMode.None);
            var count = Math.Min(16, encrypted.Length - offset);
            for (var i = 0; i < count; i++)
                plaintext[offset + i] = (byte)(encrypted[offset + i] ^ block[i]);
        }

        return plaintext;
    }

    /// <summary>Create a fresh P-256 private key for one negotiation.</summary>
    public static ECDiffieHellman GenerateEcdhKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>
    /// Return the public point of a key as the raw X || Y bytes. Anker devices
    /// exchange the uncompressed point without its 04 prefix.
    /// </summary>
    public static byte[] EcdhPublicBytes(ECDiffieHellman key)
    {
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        return [.. q.X!, .. q.Y!];
    }

    /// <summary>Derive the 32-byte ECDH shared secret from the device's raw X || Y public point.</summary>
    public static byte[] EcdhSharedSecret(ECDiffieHellman key, byte[] devicePublicBytes)
    {
        if (devicePublicBytes.Length != 64)
            throw new FormatException($"Device public key is {devicePublicBytes.Length} bytes, expected 64!");

        using var devicePublic = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = devicePublicBytes[..32],
                Y = devicePublicBytes[32..],
            },
        });
        using var devicePublicKey = devicePublic.PublicKey;
        return key.DeriveRawSecretAgreement(devicePublicKey);
    }
}
