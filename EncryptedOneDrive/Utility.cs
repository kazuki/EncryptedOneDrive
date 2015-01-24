// Copyright (C) 2014  Kazuki Oikawa
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

        public static string GetParentPath (string path)
        {
            string name;
            return GetParentPath (path, out name);
        }

        public static string GetParentPath (string path, out string name)
        {
            while (path.Length > 0 && path[path.Length - 1] == '/')
                path = path.Substring (0, path.Length - 1);
            int pos = path.LastIndexOf ('/');
            if (pos <= 0) {
                name = path.Substring (1);
                return "/";
            }
            name = path.Substring (pos + 1);
            return path.Substring (0, pos);
        }

        public static string CombinePath (params string[] list)
        {
            StringBuilder sb = new StringBuilder ();
            for (var i = 0; i < list.Length; ++i) {
                sb.Append (list [i]);
                if (sb [sb.Length - 1] != '/' && i < list.Length - 1)
                    sb.Append ('/');
            }
            return sb.ToString ();
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
