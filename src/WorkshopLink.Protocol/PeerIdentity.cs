extern alias modern;

using System.Security.Cryptography;
using System.Text;
using modern::Org.BouncyCastle.Crypto.Generators;
using modern::Org.BouncyCastle.Crypto.Parameters;
using modern::Org.BouncyCastle.Crypto.Signers;
using modern::Org.BouncyCastle.Security;

namespace WorkshopLink.Protocol;

public static class PeerIdentity
{
    private static Ed25519PrivateKeyParameters? _private;
    public static string PublicKey { get; private set; } = "";
    public static string Fingerprint { get; private set; } = "";

    public static void Load(string path, bool machine = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] seed;
        if (File.Exists(path))
        {
            seed = Unprotect(File.ReadAllBytes(path), machine);
        }
        else
        {
            var generator = new Ed25519KeyPairGenerator();
            generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            seed = ((Ed25519PrivateKeyParameters)generator.GenerateKeyPair().Private).GetEncoded();
        }

        if (seed.Length < 32)
            throw new InvalidOperationException("The saved identity is not an Ed25519 key.");
        try
        {
            File.WriteAllBytes(path, Protect(seed.AsSpan(0, 32).ToArray(), machine));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }

        var key = new Ed25519PrivateKeyParameters(seed, 0);
        _private = key;
        var raw = key.GeneratePublicKey().GetEncoded();
        PublicKey = Convert.ToHexString(raw);
        Fingerprint = Convert.ToHexString(SHA256.HashData(raw).AsSpan(0, 8));
    }

    public static string Sign(string text)
    {
        if (_private is null)
            throw new InvalidOperationException("This computer has no directory key yet.");
        var signer = new Ed25519Signer();
        signer.Init(true, _private);
        var bytes = Encoding.UTF8.GetBytes(text);
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return Convert.ToHexString(signer.GenerateSignature());
    }

    public static bool Verify(string publicKeyHex, string signatureHex, string text)
    {
        try
        {
            var pub = Convert.FromHexString(publicKeyHex.Trim());
            var sig = Convert.FromHexString(signatureHex.Trim());
            if (pub.Length != 32 || sig.Length != 64)
                return false;
            var signer = new Ed25519Signer();
            signer.Init(false, new Ed25519PublicKeyParameters(pub, 0));
            var bytes = Encoding.UTF8.GetBytes(text);
            signer.BlockUpdate(bytes, 0, bytes.Length);
            return signer.VerifySignature(sig);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static readonly byte[] Entropy = "WorkshopLink-identity"u8.ToArray();

    private static byte[] Protect(byte[] seed, bool machine)
    {
        try
        {
            var scope = machine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser;
            return ProtectedData.Protect(seed, Entropy, scope);
        }
        catch (PlatformNotSupportedException)
        {
            return seed;
        }
    }

    private static byte[] Unprotect(byte[] stored, bool machine)
    {
        if (stored.Length == 32)
            return stored;
        try
        {
            var scope = machine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser;
            return ProtectedData.Unprotect(stored, Entropy, scope);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            return stored.Length >= 32 ? stored.AsSpan(0, 32).ToArray() : stored;
        }
    }
}
