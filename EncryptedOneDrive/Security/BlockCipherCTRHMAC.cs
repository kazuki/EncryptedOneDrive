// Copyright (C) 2014-2015  Kazuki Oikawa
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Security.Cryptography;

namespace EncryptedOneDrive.Security
{
    public class BlockCipherCTRHMAC<TCiper, THMAC> : IAuthenticatedEncryptionAlgorithm where TCiper: SymmetricAlgorithm, new() where THMAC : HMAC, new()
    {
        public BlockCipherCTRHMAC()
        {
            SymmetricAlgorithmInstance = new TCiper ();
            SymmetricAlgorithmInstance.Mode = CipherMode.ECB;

            var hmac = new THMAC ();
            int hmacBlockSize;

            switch (hmac.HashName.ToUpper ()) {
            case "MD5":
            case "SHA1":
            case "SHA224":
            case "SHA256":
                hmacBlockSize = 64;
                break;
            case "SHA384":
            case "SHA512":
            case "SHA512/224":
            case "SHA512/256":
                hmacBlockSize = 128;
                break;
            default:
                throw new NotSupportedException ();
            }

            KeyByteSize = SymmetricAlgorithmInstance.KeySize / 8 + hmacBlockSize;
            IVByteSize = SymmetricAlgorithmInstance.BlockSize / 8;
            TagByteSize = hmac.HashSize / 8;
        }

        TCiper SymmetricAlgorithmInstance { get; set; }
        public int KeyByteSize { get; private set; }
        public int IVByteSize { get; private set; }
        public int TagByteSize { get; private set; }

        public IAuthenticatedCryptoTransform CreateEncryptor (byte[] key, byte[] iv, byte[] aad)
        {
            if (key.Length != KeyByteSize || iv.Length != IVByteSize)
                throw new ArgumentException ();
            return new Transform (this, key, iv, aad, null, true);
        }

        public IAuthenticatedCryptoTransform CreateDecryptor (byte[] key, byte[] iv, byte[] aad, byte[] tag)
        {
            if (key.Length != KeyByteSize || iv.Length != IVByteSize || (tag != null && tag.Length != TagByteSize))
                throw new ArgumentException ();
            return new Transform (this, key, iv, aad, tag, false);
        }

        class Transform : IAuthenticatedCryptoTransform
        {
            readonly ICryptoTransform _ct;
            readonly KeyedHashAlgorithm _mac;
            byte[] _counter;
            byte[] _cipher;
            byte[] _tag;
            int _pos = 0;
            bool _encryptMode;

            public Transform (BlockCipherCTRHMAC<TCiper,THMAC> owner, byte[] key, byte[] iv, byte[] aad, byte[] tag, bool isEncrypt)
            {
                byte[] cipherKey = new byte[owner.SymmetricAlgorithmInstance.KeySize / 8];
                byte[] hmacKey = new byte[key.Length - cipherKey.Length];
                Buffer.BlockCopy (key, 0, cipherKey, 0, cipherKey.Length);
                Buffer.BlockCopy (key, cipherKey.Length, hmacKey, 0, hmacKey.Length);
                _tag = tag;
                _encryptMode = isEncrypt;

                _ct = owner.SymmetricAlgorithmInstance.CreateEncryptor (cipherKey, null);
                _cipher = new byte[iv.Length];
                _pos = 0;
                _mac = new THMAC();
                _mac.Key = hmacKey;
                _mac.Initialize();
                _counter = (byte[])iv.Clone();
            }

            public void TransformBlock (byte[] inputBuffer, int inputOffset, int inputCount, byte[] outputBuffer, int outputOffset)
            {
                byte[] cipher = _cipher;
                byte[] counter = _counter;
                int outputOffsetBackup = outputOffset;
                int outputCount = inputCount;

                if (!_encryptMode) {
                    _mac.TransformBlock (inputBuffer, inputOffset, inputCount, inputBuffer, inputOffset);
                }

                if (_pos > 0) {
                    int size = Math.Min (cipher.Length - _pos, inputCount);
                    for (int i = 0; i < size; ++i) {
                        outputBuffer [outputOffset + i] = (byte)(inputBuffer [inputOffset + i] ^ cipher [_pos + i]);
                    }
                    _pos += size;
                    if (inputCount == size)
                        goto ComputeMAC;
                    inputCount -= size;
                    inputOffset += size;
                    outputOffset += size;
                }

                while (inputCount >= cipher.Length) {
                    updateCipherText (counter, cipher);
                    for (int i = 0; i < cipher.Length; ++i) {
                        outputBuffer [outputOffset + i] = (byte)(inputBuffer [inputOffset + i] ^ cipher [i]);
                    }
                    outputOffset += cipher.Length;
                    inputOffset += cipher.Length;
                    inputCount -= cipher.Length;
                }

                if (inputCount > 0) {
                    updateCipherText (counter, cipher);
                    for (int i = 0; i < inputCount; ++i) {
                        outputBuffer [outputOffset + i] = (byte)(inputBuffer [inputOffset + i] ^ cipher [i]);
                    }
                    _pos = inputCount;
                }

            ComputeMAC:
                if (_encryptMode) {
                    _mac.TransformBlock (outputBuffer, outputOffsetBackup, outputCount, outputBuffer, outputOffsetBackup);
                }
            }

            void updateCipherText (byte[] counter, byte[] cipher)
            {
                _ct.TransformBlock (counter, 0, counter.Length, cipher, 0);
                _pos = 0;
                for (int i = counter.Length - 1; i >= 0 && ++_counter [i] == 0; --i);
            }

            public byte[] TransformFinal ()
            {
                if (_cipher == null)
                    throw new InvalidOperationException ();

                Array.Clear (_cipher, 0, _cipher.Length);
                Array.Clear (_counter, 0, _counter.Length);
                _cipher = _counter = null;
                _mac.TransformFinalBlock (new byte[0], 0, 0);
                byte[] tag = _mac.Hash;
                if (_tag != null) {
                    for (int i = 0; i < tag.Length; ++i)
                        if (tag [i] != _tag [i])
                            throw new CryptographicException ();
                }
                return tag;
            }

            public void Dispose ()
            {
                _ct.Dispose ();
                _mac.Dispose ();
            }
        }
    }
}

