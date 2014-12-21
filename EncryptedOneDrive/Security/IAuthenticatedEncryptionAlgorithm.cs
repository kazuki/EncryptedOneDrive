using System;
using System.Security.Cryptography;

namespace EncryptedOneDrive
{
    public interface IAuthenticatedEncryptionAlgorithm
    {
        int KeyByteSize { get; }
        int IVByteSize { get; }
        int TagByteSize { get; }

        IAuthenticatedCryptoTransform CreateEncryptor(byte[] key, byte[] iv, byte[] aad);
        IAuthenticatedCryptoTransform CreateDecryptor(byte[] key, byte[] iv, byte[] aad, byte[] tag);
    }
}
