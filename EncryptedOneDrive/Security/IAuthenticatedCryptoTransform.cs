using System;

namespace EncryptedOneDrive
{
    public interface IAuthenticatedCryptoTransform : IDisposable
    {
        void TransformBlock (byte[] inputBuffer, int inputOffset, int inputCount, byte[] outputBuffer, int outputOffset);
        byte[] TransformFinal ();
    }
}
