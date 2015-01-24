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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Mono.Fuse;
using Mono.Unix.Native;

namespace EncryptedOneDrive
{
    public class Fuse : Mono.Fuse.FileSystem
    {
        int _handleIndex = 0;
        ReaderWriterLockSlim _lock = new ReaderWriterLockSlim ();
        Dictionary<int, object> _handles = new Dictionary<int, object> ();

        static readonly DateTime EpochBase = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        readonly FileSystemBase _fs;
        readonly uint uid, gid;

        public Fuse (FileSystemBase fs)
        {
            _fs = fs;
            uid = Mono.Unix.Native.Syscall.getuid ();
            gid = Mono.Unix.Native.Syscall.getgid ();
        }

        protected override void Dispose (bool disposing)
        {
            base.Dispose (disposing);
            _fs.Dispose ();
        }

        protected override Errno OnGetPathStatus (string path, out Stat stat)
        {
            Debug.WriteLine ("OnGetPathStatus: {0}", path);
            stat = new Stat ();
            try {
                var x = _fs.Stat (path);
                CopyStat (x, ref stat);
                return 0;
            } catch (FileNotFoundException) {
                return Errno.ENOENT;
            } catch {
                return Errno.EIO;
            }
        }

        protected override Errno OnReadDirectory (string directory, OpenedPathInfo info, out IEnumerable<DirectoryEntry> paths)
        {
            Debug.WriteLine ("OnReadDirectory: {0}", directory);
            paths = null;
            var x = _fs.List (directory);
            if (x == null)
                return Errno.ENOENT;
            List<DirectoryEntry> list = new List<DirectoryEntry> (x.Length + 2);
            list.Add (new DirectoryEntry ("."));
            list.Add (new DirectoryEntry (".."));
            paths = list;
            for (int i = 0; i < x.Length; ++i) {
                DirectoryEntry entry = new DirectoryEntry (x [i].Name);
                CopyStat (x [i], ref entry.Stat);
                list.Add (entry);
            }
            return 0;
        }

        void CopyStat (FileProperty x, ref Stat s)
        {
            s.st_uid = uid;
            s.st_gid = gid;
            s.st_ctime = (long)(x.CreationTime - EpochBase).TotalSeconds;
            s.st_mtime = s.st_ctime;
            //s.st_mtime = (long)(x.LastWriteTime - EpochBase).TotalSeconds;
            if (x.IsFile) {
                s.st_mode = FilePermissions.S_IFREG | NativeConvert.FromOctalPermissionString ("0444");
                s.st_nlink = 1;
                s.st_size = x.Size;
            } else {
                s.st_mode = FilePermissions.S_IFDIR | NativeConvert.FromOctalPermissionString ("0755");
                s.st_nlink = 2;
            }
        }

        protected override Errno OnOpenHandle (string file, OpenedPathInfo info)
        {
            Debug.WriteLine ("OnOpenHandle: {0}", file);
            if (!info.OpenAccess.HasFlag (OpenFlags.O_RDONLY))
                return Errno.EACCES;

            Stream strm = _fs.ReadOpen (file);
            if (strm == null)
                return Errno.ENOENT;
            info.Handle = RegisterState (strm);
            return 0;
        }

        protected override Errno OnReadHandle (string file, OpenedPathInfo info, byte[] buf, long offset, out int bytesWritten)
        {
            Debug.WriteLine ("OnReadHandle: {0} off={1}", file, offset);
            bytesWritten = -1;
            Stream strm = GetStream (info.Handle);
            if (strm == null)
                return Errno.EIO;
            lock (strm) {
                if (!strm.CanRead)
                    return Errno.EINVAL;
                strm.Seek (offset, SeekOrigin.Begin);
                try {
                    bytesWritten = strm.Read (buf, 0, buf.Length);
                } catch (Exception e) {
                    Debug.WriteLine (e);
                    throw e;
                }
            }
            return 0;
        }

        protected override Errno OnCreateHandle (string file, OpenedPathInfo info, FilePermissions mode)
        {
            Debug.WriteLine ("OnCreateHandle: {0}", file);
            Stream strm = _fs.WriteOpen (file);
            if (strm == null)
                return Errno.EIO;
            info.Handle = RegisterState (strm);
            return 0;
        }

        protected override Errno OnWriteHandle (string file, OpenedPathInfo info, byte[] buf, long offset, out int bytesRead)
        {
            Debug.WriteLine ("OnWriteHandle: {0} off={1}", file, offset);
            bytesRead = -1;
            Stream strm = GetStream (info.Handle);
            if (strm == null || strm.Position != offset)
                return Errno.EIO;
            lock (strm) {
                if (!strm.CanWrite)
                    return Errno.EINVAL;
                try {
                    strm.Write (buf, 0, buf.Length);
                } catch (Exception e) {
                    Debug.WriteLine (e);
                    throw e;
                }
            }
            bytesRead = buf.Length;
            return 0;
        }

        protected override Errno OnFlushHandle (string file, OpenedPathInfo info)
        {
            Debug.WriteLine ("OnFlushHandle: {0}", file);
            Stream strm = GetStream (info.Handle);
            if (strm == null)
                return Errno.EIO;
            lock (strm) {
                if (strm.CanWrite)
                    strm.Flush ();
            }
            return 0;
        }

        protected override Errno OnReleaseHandle (string file, OpenedPathInfo info)
        {
            Debug.WriteLine ("OnReleaseHandle: {0}", file);
            object obj;
            using (var l = _lock.WriteLock ()) {
                if (!_handles.TryGetValue (info.Handle.ToInt32 (), out obj))
                    return Errno.EINVAL;
                _handles.Remove (info.Handle.ToInt32 ());
            }
            var strm = obj as Stream;
            if (strm != null) {
                lock (strm) {
                    strm.Close ();
                }
                return 0;
            }
            return Errno.EIO;
        }

        protected override Errno OnCreateDirectory (string directory, FilePermissions mode)
        {
            Debug.WriteLine ("OnCreateDirectory: {0}", directory);
            try {
                _fs.CreateDirectory (directory);
            } catch {
                return Errno.EIO;
            }
            return 0;
        }

        protected override Errno OnRemoveDirectory (string directory)
        {
            Debug.WriteLine ("OnRemoveDirectory: {0}", directory);
            try {
                _fs.DeleteDirectory (directory);
            } catch {
                return Errno.EIO;
            }
            return 0;
        }

        protected override Errno OnRemoveFile (string file)
        {
            Debug.WriteLine ("OnRemoveFile: {0}", file);
            try {
                _fs.DeleteFile (file);
            } catch {
                return Errno.EIO;
            }
            return 0;
        }

        IntPtr RegisterState (object state)
        {
            int handle = Interlocked.Increment (ref _handleIndex);
            using (var l = _lock.WriteLock ()) {
                _handles.Add (handle, state);
            }
            return new IntPtr (handle);
        }

        Stream GetStream (IntPtr handle)
        {
            using (var l = _lock.ReadLock ()) {
                object obj;
                if (!_handles.TryGetValue (handle.ToInt32 (), out obj))
                    return null;
                return obj as Stream;
            }
        }

        protected override Errno OnGetFileSystemStatus (string path, out Statvfs buf)
        {
            Debug.WriteLine ("OnGetFileSystemStatus: {0}", path);
            long total, avail;
            buf = new Statvfs ();
            try {
                _fs.GetStorageUsage (out total, out avail);
                buf.f_bsize = 1;
                buf.f_frsize = 1;
                buf.f_blocks = (ulong)total;
                buf.f_bfree = (ulong)avail;
                buf.f_bavail = (ulong)avail;
                buf.f_files = 0;
                buf.f_ffree = 0;
                buf.f_favail = 0;
                buf.f_fsid = 0;
                buf.f_flag = 0;
                buf.f_namemax = 1024;
                return 0;
            } catch {
                return Errno.EIO;
            }
        }

        #if false
        protected override Errno OnAccessPath (string path, AccessModes mode)
        {
            Debug.WriteLine ("OnAccessPath: {0}", path);
            return base.OnAccessPath (path, mode);
        }

        protected override Errno OnGetHandleStatus (string file, OpenedPathInfo info, out Stat buf)
        {
            Debug.WriteLine ("OnGetHandleStatus: {0}", file);
            return base.OnGetHandleStatus (file, info, out buf);
        }
        #endif
    }
}
