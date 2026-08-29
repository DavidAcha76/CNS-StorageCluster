using System.Security.Cryptography;
using System.Text;

namespace CNS.StorageCluster.Shared;

/// <summary>
/// Cifra los mensajes de aplicacion que viajan entre el cliente y el servidor.
/// El resultado es texto Base64 seguro para el framing por linea de TCP y los
/// mensajes de texto WebSocket, pero su contenido es siempre cifrado.
/// </summary>
public sealed class TransportCipher
{
    public const string EncryptionKeyEnvironmentVariable = "CNS_STORAGE_CLUSTER_ENCRYPTION_KEY";
    public const string EncryptionEnabledEnvironmentVariable = "CNS_STORAGE_CLUSTER_ENABLE_ENCRYPTION";

    private const string EnvelopePrefix = "CNS1:";
    private const int KeySizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const int MaximumPlaintextBytes = 1024 * 1024;
    private const string LocalKeyFileName = "transport-key.base64";
    private static readonly byte[] AssociatedData = "CNS.StorageCluster.Transport.v1"u8.ToArray();

    private readonly byte[]? _key;
    private readonly bool _plaintextMode;

    private TransportCipher(byte[] key) => _key = key;
    private TransportCipher() => _plaintextMode = true;

    public static TransportCipher FromEnvironment()
    {
        if (!IsEncryptionEnabled()) return new TransportCipher();

        var encodedKey = Environment.GetEnvironmentVariable(EncryptionKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(encodedKey) && OperatingSystem.IsWindows())
        {
            // Visual Studio y los servicios ya iniciados no actualizan su entorno
            // cuando se crea una variable de usuario. Leer el valor persistente
            // permite usar la misma clave sin incorporar secretos al proyecto.
            encodedKey = Environment.GetEnvironmentVariable(
                EncryptionKeyEnvironmentVariable,
                EnvironmentVariableTarget.User);
        }
        if (string.IsNullOrWhiteSpace(encodedKey))
        {
            encodedKey = GetOrCreateLocalKey();
        }

        return FromBase64Key(encodedKey);
    }

    private static bool IsEncryptionEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable(EncryptionEnabledEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    private static string GetOrCreateLocalKey()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localData))
        {
            try
            {
                return GetOrCreateKeyAt(Path.Combine(localData, "CNS.StorageCluster", "secrets"));
            }
            catch (UnauthorizedAccessException)
            {
                // Algunas ejecuciones restringidas no pueden escribir en el perfil
                // del usuario. Se usa entonces el directorio de la aplicacion.
            }
        }

        return GetOrCreateKeyAt(Path.Combine(AppContext.BaseDirectory, ".cns-storagecluster", "secrets"));
    }

    private static string GetOrCreateKeyAt(string folder)
    {
        var keyPath = Path.Combine(folder, LocalKeyFileName);
        Directory.CreateDirectory(folder);

        if (File.Exists(keyPath))
        {
            return File.ReadAllText(keyPath).Trim();
        }

        var generatedKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeySizeBytes));
        try
        {
            using var stream = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(generatedKey);
            return generatedKey;
        }
        catch (IOException) when (File.Exists(keyPath))
        {
            // Otro proceso inició a la vez y ya creó la clave compartida local.
            return File.ReadAllText(keyPath).Trim();
        }
    }

    public static TransportCipher FromBase64Key(string encodedKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedKey);

        byte[] key;
        try
        {
            key = Convert.FromBase64String(encodedKey.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("La clave de transporte debe estar codificada en Base64.", ex);
        }

        if (key.Length != KeySizeBytes)
        {
            throw new InvalidOperationException("La clave de transporte debe tener exactamente 32 bytes (AES-256).");
        }

        return new TransportCipher(key);
    }

    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (_plaintextMode) return plaintext;

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        if (plaintextBytes.Length > MaximumPlaintextBytes)
        {
            throw new InvalidOperationException("El mensaje supera el tamaño máximo permitido para el transporte cifrado.");
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSizeBytes];

        using (var aes = new AesGcm(_key!, TagSizeBytes))
        {
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, AssociatedData);
        }

        var envelope = new byte[NonceSizeBytes + TagSizeBytes + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, envelope, 0, NonceSizeBytes);
        Buffer.BlockCopy(tag, 0, envelope, NonceSizeBytes, TagSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, envelope, NonceSizeBytes + TagSizeBytes, ciphertext.Length);
        return EnvelopePrefix + Convert.ToBase64String(envelope);
    }

    public string Decrypt(string encryptedEnvelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedEnvelope);
        if (_plaintextMode) return encryptedEnvelope;
        if (!encryptedEnvelope.StartsWith(EnvelopePrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Se recibió un mensaje sin el formato de transporte cifrado esperado.");
        }

        var encodedPayload = encryptedEnvelope[EnvelopePrefix.Length..];
        var maximumEnvelopeLength = NonceSizeBytes + TagSizeBytes + MaximumPlaintextBytes;
        if (encodedPayload.Length > ((maximumEnvelopeLength + 2) / 3) * 4)
        {
            throw new InvalidDataException("El mensaje cifrado supera el tamaño máximo permitido.");
        }

        byte[] envelope;
        try
        {
            envelope = Convert.FromBase64String(encodedPayload);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("El mensaje cifrado no está codificado correctamente.", ex);
        }

        if (envelope.Length < NonceSizeBytes + TagSizeBytes || envelope.Length > maximumEnvelopeLength)
        {
            throw new InvalidDataException("El mensaje cifrado tiene un tamaño no válido.");
        }

        var ciphertextLength = envelope.Length - NonceSizeBytes - TagSizeBytes;
        var plaintext = new byte[ciphertextLength];
        using (var aes = new AesGcm(_key!, TagSizeBytes))
        {
            aes.Decrypt(
                envelope.AsSpan(0, NonceSizeBytes),
                envelope.AsSpan(NonceSizeBytes + TagSizeBytes, ciphertextLength),
                envelope.AsSpan(NonceSizeBytes, TagSizeBytes),
                plaintext,
                AssociatedData);
        }

        return Encoding.UTF8.GetString(plaintext);
    }
}
