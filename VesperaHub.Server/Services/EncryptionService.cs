using System.Security.Cryptography;
using System.Text;

namespace VesperaHub.Server.Services;

/// <summary>
/// Encrypts message content at rest with AES-256-GCM (authenticated encryption).
/// Output format (Base64): nonce (12 bytes) | tag (16 bytes) | ciphertext.
/// </summary>
public sealed class EncryptionService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public EncryptionService(IConfiguration config)
    {
        var base64 = config["Encryption:Key"]
            ?? throw new InvalidOperationException("Encryption:Key is not configured.");
        _key = Convert.FromBase64String(base64);
        if (_key.Length != 32)
            throw new InvalidOperationException("Encryption:Key must be a Base64-encoded 32-byte key.");
    }

    public string Encrypt(string plainText)
    {
        byte[] plain = Encoding.UTF8.GetBytes(plainText);
        byte[] buffer = new byte[NonceSize + TagSize + plain.Length];
        Span<byte> nonce = buffer.AsSpan(0, NonceSize);
        Span<byte> tag = buffer.AsSpan(NonceSize, TagSize);
        Span<byte> cipher = buffer.AsSpan(NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        return Convert.ToBase64String(buffer);
    }

    public string Decrypt(string encryptedBase64)
    {
        byte[] buffer = Convert.FromBase64String(encryptedBase64);
        if (buffer.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext is too short.");

        ReadOnlySpan<byte> nonce = buffer.AsSpan(0, NonceSize);
        ReadOnlySpan<byte> tag = buffer.AsSpan(NonceSize, TagSize);
        ReadOnlySpan<byte> cipher = buffer.AsSpan(NonceSize + TagSize);
        byte[] plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }
}
