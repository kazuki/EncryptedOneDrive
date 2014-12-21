using System;
using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace EncryptedOneDrive
{
    public class CryptoManager
    {
        public CryptoManager (string password, string salt)
        {
            this.RNG = new RNGCryptoServiceProvider ();
            this.AuthenticatedEncryption = new BlockCiperCTRHMAC<AesCryptoServiceProvider, HMACSHA256> ();

            var kdf = new Rfc2898DeriveBytes (Encoding.UTF8.GetBytes (password),
                          Encoding.UTF8.GetBytes (salt), 10000);
            this.AEKey = kdf.GetBytes (this.AuthenticatedEncryption.KeyByteSize);
        }

        public RandomNumberGenerator RNG { get; private set; }
        public IAuthenticatedEncryptionAlgorithm AuthenticatedEncryption { get; private set; }
        byte[] AEKey { get; set; }

        public byte[] GetRandomBytes (int count)
        {
            byte[] rnd = new byte[count];
            lock (this.RNG) {
                this.RNG.GetBytes (rnd);
            }
            return rnd;
        }

        public IAuthenticatedCryptoTransform CreateAuthenticatedEncryptor (byte[] iv, byte[] aad)
        {
            return this.AuthenticatedEncryption.CreateEncryptor (AEKey, iv, aad);
        }

        public IAuthenticatedCryptoTransform CreateAuthenticatedDecryptor (byte[] iv, byte[] aad, byte[] tag)
        {
            return this.AuthenticatedEncryption.CreateDecryptor (AEKey, iv, aad, tag);
        }

        public Stream WrapInEncryptor (Stream strm, out byte[] outputTag)
        {
            outputTag = new byte[AuthenticatedEncryption.TagByteSize];
            return new WrapStream (strm, this, outputTag, true);
        }

        public Stream WrapInDecryptor (Stream strm, byte[] tag)
        {
            return new WrapStream (strm, this, tag, false);
        }

        class WrapStream : Stream
        {
            Stream _strm;
            IAuthenticatedCryptoTransform _act;
            bool _encryptMode;
            byte[] _tag = null;
            int _ivSize;

            public WrapStream (Stream baseStream, CryptoManager mgr, byte[] tag, bool encryptMode)
            {
                _strm = baseStream;
                _encryptMode = encryptMode;
                _ivSize = mgr.AuthenticatedEncryption.IVByteSize;
                if (encryptMode) {
                    // encrypt mode
                    _tag = tag;
                    byte[] iv = mgr.GetRandomBytes(_ivSize);
                    baseStream.Write(iv, 0, iv.Length);
                    _act = mgr.CreateAuthenticatedEncryptor (iv, null);
                } else {
                    // decrypt mode
                    byte[] iv = new byte[_ivSize];
                    baseStream.ReadFull (iv, 0, iv.Length);
                    _act = mgr.CreateAuthenticatedDecryptor (iv, null, tag);
                }
            }

            public override int Read (byte[] buffer, int offset, int count)
            {
                if (_encryptMode)
                    throw new InvalidOperationException ();
                int size = _strm.Read (buffer, offset, count);
                _act.TransformBlock (buffer, offset, size, buffer, offset);
                return size;
            }

            public override void Write (byte[] buffer, int offset, int count)
            {
                if (!_encryptMode)
                    throw new InvalidOperationException ();
                _act.TransformBlock (buffer, offset, count, buffer, offset);
                _strm.Write (buffer, offset, count);
            }

            public override void Flush ()
            {
                _strm.Flush ();
            }

            public override void Close ()
            {
                if (_act == null)
                    return;

                base.Close ();
                _strm.Close ();
                byte[] tag = _act.TransformFinal ();
                if (_encryptMode)
                    Buffer.BlockCopy (tag, 0, _tag, 0, tag.Length);
                _act = null;
            }

            public override long Seek (long offset, SeekOrigin origin)
            {
                throw new NotSupportedException ();
            }
            public override void SetLength (long value)
            {
                throw new NotSupportedException ();
            }
            public override bool CanRead {
                get { return !_encryptMode; }
            }
            public override bool CanSeek {
                get { return false; }
            }
            public override bool CanWrite {
                get { return _encryptMode; }
            }
            public override long Length {
                get { return _strm.Length; }
            }
            public override long Position {
                get { return _strm.Position - _ivSize; }
                set { throw new NotSupportedException (); }
            }
        }
    }
}
