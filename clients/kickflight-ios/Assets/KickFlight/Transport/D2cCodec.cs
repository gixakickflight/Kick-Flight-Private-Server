using System;
using System.Security.Cryptography;

namespace KickFlight.Transport
{
    /// <summary>
    /// GRE/D2C envelope used by the existing server: a 16-byte IV followed by AES-256-CBC/PKCS7 bytes.
    /// This class does not know or store any protocol key.
    /// </summary>
    public static class D2cCodec
    {
        public const int KeySizeBytes = 32;
        public const int IvSizeBytes = 16;
        public const int BlockSizeBytes = 16;

        public static byte[] Encode(byte[] plaintext, byte[] key, byte[] iv)
        {
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
            ValidateKey(key);
            ValidateIv(iv);

            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = (byte[])key.Clone();
                aes.IV = (byte[])iv.Clone();

                using (var encryptor = aes.CreateEncryptor())
                {
                    var ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
                    var envelope = new byte[IvSizeBytes + ciphertext.Length];
                    Buffer.BlockCopy(iv, 0, envelope, 0, IvSizeBytes);
                    Buffer.BlockCopy(ciphertext, 0, envelope, IvSizeBytes, ciphertext.Length);
                    return envelope;
                }
            }
        }

        public static byte[] EncodeWithRandomIv(byte[] plaintext, byte[] key)
        {
            ValidateKey(key);
            var iv = new byte[IvSizeBytes];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(iv);
            return Encode(plaintext, key, iv);
        }

        public static byte[] Decode(byte[] envelope, byte[] key)
        {
            ValidateKey(key);
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            if (envelope.Length < IvSizeBytes + BlockSizeBytes)
                throw new FormatException("D2C envelope must contain a 16-byte IV and at least one AES block.");
            if (envelope.Length % BlockSizeBytes != 0)
                throw new FormatException("D2C envelope length must be a multiple of 16 bytes.");

            var iv = new byte[IvSizeBytes];
            Buffer.BlockCopy(envelope, 0, iv, 0, IvSizeBytes);
            var ciphertextLength = envelope.Length - IvSizeBytes;
            var ciphertext = new byte[ciphertextLength];
            Buffer.BlockCopy(envelope, IvSizeBytes, ciphertext, 0, ciphertextLength);

            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = (byte[])key.Clone();
                aes.IV = iv;
                using (var decryptor = aes.CreateDecryptor())
                    return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            }
        }

        /// <summary>Validates the server's auth hash convention: exactly 32 ASCII characters.</summary>
        public static byte[] KeyFromAscii32(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.Length != KeySizeBytes)
                throw new ArgumentException("Protocol key text must contain exactly 32 ASCII characters.", nameof(value));

            var key = new byte[KeySizeBytes];
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] > 0x7f)
                    throw new ArgumentException("Protocol key text must be ASCII.", nameof(value));
                key[i] = (byte)value[i];
            }
            return key;
        }

        private static void ValidateKey(byte[] key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (key.Length != KeySizeBytes)
                throw new ArgumentException("D2C key must contain exactly 32 bytes.", nameof(key));
        }

        private static void ValidateIv(byte[] iv)
        {
            if (iv == null) throw new ArgumentNullException(nameof(iv));
            if (iv.Length != IvSizeBytes)
                throw new ArgumentException("D2C IV must contain exactly 16 bytes.", nameof(iv));
        }
    }
}
