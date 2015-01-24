using System;
using System.Collections.Generic;
using EncryptedOneDrive;

namespace EncryptedOneDrive.Tests
{
    public static class TestUtility
    {
        public class DummyKVS : IKeyValueStore
        {
            static DummyKVS instance = new DummyKVS ();
            private DummyKVS() {}
            public static IKeyValueStore Instance {
                get { return instance; }
            }

            public void Opne (string path)
            {
            }
            public byte[] Get (string key)
            {
                throw new KeyNotFoundException ();
            }
            public void Put (string key, byte[] value)
            {
            }
            public void Delete (string key)
            {
            }

            public void Dispose ()
            {
            }
        }
    }
}

