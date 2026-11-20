using System.Security.Cryptography;
using System.Text;

namespace VesperaHub.Server.Services;

public class EncryptionService
{
    // 32-byte key for AES-256 (In production, load from Azure Key Vault / environment secrets)
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("VesperaHubSecretKey2026!@#123456");

    public string Encrypt(string plainText)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Prepend IV to ciphertext so the receiver can decrypt it
        byte[] combined = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, combined, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, combined, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(combined);
    }

    public string Decrypt(string encryptedBase64)
    {
        byte[] combined = Convert.FromBase64String(encryptedBase64);
        using var aes = Aes.Create();
        aes.Key = Key;

        byte[] iv = new byte[aes.BlockSize / 8];
        byte[] cipherBytes = new byte[combined.Length - iv.Length];

        Buffer.BlockCopy(combined, 0, iv, 0, iv.Length);
        Buffer.BlockCopy(combined, iv.Length, cipherBytes, 0, cipherBytes.Length);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

        return Encoding.UTF8.GetString(plainBytes);
    }
}