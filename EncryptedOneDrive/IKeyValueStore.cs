using System;

namespace EncryptedOneDrive
{
    public interface IKeyValueStore : IDisposable
    {
        void Opne (string path);

        byte[] Get (string key);
        void Put (string key, byte[] value);
        void Delete (string key);
    }
}
