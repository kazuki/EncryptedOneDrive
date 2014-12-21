using System;
using System.IO;
using System.Threading;
using System.Text;

namespace EncryptedOneDrive
{
    public static class Utility
    {
        public static IDisposable ReadLock (this ReaderWriterLockSlim l)
        {
            l.EnterReadLock ();
            return new LockState (l, 0);
        }

        public static IDisposable UpgradeableReadLock (this ReaderWriterLockSlim l)
        {
            l.EnterUpgradeableReadLock ();
            return new LockState (l, 1);
        }

        public static IDisposable WriteLock (this ReaderWriterLockSlim l)
        {
            l.EnterWriteLock ();
            return new LockState (l, 2);
        }

        public static void ReadFull (this Stream strm, byte[] buf, int offset, int count)
        {
            while (count > 0) {
                int ret = strm.Read (buf, offset, count);
                if (ret <= 0)
                    throw new IOException();
                offset += ret;
                count -= ret;
            }
        }

        public static string ToHexString (this byte[] buf)
        {
            StringBuilder sb = new StringBuilder (buf.Length * 2);
            for (int i = 0; i < buf.Length; ++i)
                sb.Append (buf [i].ToString ("x2"));
            return sb.ToString ();
        }

        public static byte[] ParseHexString (string hexText)
        {
            if (hexText.Length % 2 == 1)
                throw new FormatException ();
            byte[] output = new byte[hexText.Length / 2];
            for (int i = 0; i < hexText.Length; i += 2) {
                output [i / 2] = byte.Parse (hexText.Substring (i, 2), System.Globalization.NumberStyles.HexNumber);
            }
            return output;
        }

        class LockState : IDisposable
        {
            ReaderWriterLockSlim _rwl;
            readonly int _type;

            public LockState (ReaderWriterLockSlim rwl, int type)
            {
                _rwl = rwl;
                _type = type;
            }

            public void Dispose ()
            {
                if (_rwl != null) {
                    switch (_type) {
                    case 0:
                        _rwl.ExitReadLock ();
                        break;
                    case 1:
                        _rwl.ExitUpgradeableReadLock ();
                        break;
                    case 2:
                        _rwl.ExitWriteLock ();
                        break;
                    }
                    _rwl = null;
                }
            }
        }
    }
}
