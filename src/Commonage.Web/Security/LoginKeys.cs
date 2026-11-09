using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Commonage.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Security;

/// <summary>
/// The keys that sign logins, setup links and form tokens, and that encrypt authenticator secrets, are kept in the
/// database, so that logins survive a new container and a restore. On their own they would let anyone with a copy of the
/// database, such as a stolen backup, forge a login. So they are encrypted with a key that is not in the database: the
/// server's COMMONAGE_LOGIN_KEY, in its .env file.
/// </summary>
public static class LoginKeys
{
    public const string Setting = "DataProtection:Key";
    public const string EnvironmentName = "COMMONAGE_LOGIN_KEY";
    public const int KeyBytes = 32;

    /// <summary>The key from the configuration, or null without one. A key that is set but unusable stops the app.</summary>
    public static byte[]? Read(IConfiguration configuration)
    {
        var text = configuration[Setting]?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        try
        {
            var key = Convert.FromBase64String(text);
            if (key.Length == KeyBytes)
                return key;
        }
        catch (FormatException)
        {
        }
        throw new InvalidOperationException(
            $"{EnvironmentName} must be {KeyBytes} random bytes in Base64, for example from \"openssl rand -base64 {KeyBytes}\". See docs/deployment.md.");
    }

    public static IServiceCollection AddLoginKeys(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDataProtection().PersistKeysToDbContext<CommonageDbContext>();
        var key = Read(configuration);
        services.AddSingleton(new LoginKeyStatus(key));
        if (key is not null)
            services.Configure<KeyManagementOptions>(options => options.XmlEncryptor = new LoginKeyEncryptor(key));
        return services;
    }

    /// <summary>
    /// Once a key is set, the keys written before it, still readable in the database, are removed, so a copy of the
    /// database no longer holds anything usable. Everyone logs in again once, and setup links made before stop working.
    /// </summary>
    public static async Task<int> RemoveUnencryptedAsync(CommonageDbContext db, LoginKeyStatus status, ILogger logger)
    {
        if (!status.Encrypted)
            return 0;
        var unencrypted = await db.DataProtectionKeys
            .Where(k => k.Xml != null && k.Xml.Contains("requiresEncryption=\"true\""))
            .ToListAsync();
        if (unencrypted.Count == 0)
            return 0;
        db.DataProtectionKeys.RemoveRange(unencrypted);
        await db.SaveChangesAsync();
        logger.LogWarning(
            "Removed {Count} login keys that were stored without encryption, now that {Name} is set. Everyone has to log in again once, and setup links created before stop working.",
            unencrypted.Count, EnvironmentName);
        return unencrypted.Count;
    }

    /// <summary>A short fingerprint of the key, so that a different key gives a clear message instead of a broken login.</summary>
    internal static string Check(byte[] key) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, "Commonage login key check"u8))[..16];
}

/// <summary>Whether the keys in the database are encrypted, for the admins' notice.</summary>
public sealed class LoginKeyStatus(byte[]? key)
{
    internal byte[]? Key { get; } = key;

    public bool Encrypted => Key is not null;
}

/// <summary>Encrypts a key with AES-GCM before it is stored. Each key gets its own random nonce.</summary>
public sealed class LoginKeyEncryptor(byte[] key) : IXmlEncryptor
{
    internal static readonly XNamespace Namespace = "https://commonage.org/2026/login-key";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[plaintext.Length];
        using (var aes = new AesGcm(key, tag.Length))
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        CryptographicOperations.ZeroMemory(plaintext);

        var element = new XElement(Namespace + "encryptedKey",
            new XComment($" Encrypted with {LoginKeys.EnvironmentName}. Without that key, it cannot be read. "),
            new XElement(Namespace + "keyCheck", LoginKeys.Check(key)),
            new XElement(Namespace + "value", Convert.ToBase64String([.. nonce, .. tag, .. ciphertext])));
        return new EncryptedXmlInfo(element, typeof(LoginKeyDecryptor));
    }
}

/// <summary>Created by Data Protection for each encrypted key it reads, with the app's services.</summary>
public sealed class LoginKeyDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var key = services.GetService<LoginKeyStatus>()?.Key
            ?? throw new CryptographicException(
                $"This login key is encrypted, but {LoginKeys.EnvironmentName} is not set. Everyone has to log in again. See docs/operations.md.");
        if ((string?)encryptedElement.Element(LoginKeyEncryptor.Namespace + "keyCheck") != LoginKeys.Check(key))
            throw new CryptographicException(
                $"This login key was encrypted with a different {LoginKeys.EnvironmentName}. Everyone has to log in again. See docs/operations.md.");

        var data = Convert.FromBase64String((string?)encryptedElement.Element(LoginKeyEncryptor.Namespace + "value") ?? "");
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;
        var plaintext = new byte[data.Length - nonceSize - tagSize];
        using (var aes = new AesGcm(key, tagSize))
            aes.Decrypt(data.AsSpan(0, nonceSize), data.AsSpan(nonceSize + tagSize), data.AsSpan(nonceSize, tagSize), plaintext);
        try
        {
            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
