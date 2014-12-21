using System;
using System.IO;
using System.Collections.Generic;
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
        readonly EncryptedOneDrive.FileSystem _fs;
        readonly uint uid, gid;

        public Fuse (EncryptedOneDrive.FileSystem fs)
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
            Console.WriteLine ("OnGetPathStatus: {0}", path);
            stat = new Stat ();
            var x = _fs.Stat (path);
            if (x == null)
                return Errno.ENOENT;
            CopyStat (x, ref stat);
            return 0;
        }

        protected override Errno OnReadDirectory (string directory, OpenedPathInfo info, out IEnumerable<DirectoryEntry> paths)
        {
            Console.WriteLine ("OnReadDirectory: {0}", directory);
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

        void CopyStat (FileSystem.Entry x, ref Stat s)
        {
            s.st_uid = uid;
            s.st_gid = gid;
            s.st_ctime = (long)(x.CreationTimeUtc - EpochBase).TotalSeconds;
            s.st_mtime = (long)(x.LastWriteTimeUtc - EpochBase).TotalSeconds;
            if (x.IsFile) {
                FileSystem.FileEntry e = (FileSystem.FileEntry)x;
                s.st_mode = FilePermissions.S_IFREG | NativeConvert.FromOctalPermissionString ("0444");
                s.st_nlink = 1;
                s.st_size = e.Size;
            } else {
                s.st_mode = FilePermissions.S_IFDIR | NativeConvert.FromOctalPermissionString ("0755");
                s.st_nlink = 2;
            }
        }

        protected override Errno OnOpenHandle (string file, OpenedPathInfo info)
        {
            Console.WriteLine ("OnOpenHandle: {0}", file);
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
            Console.WriteLine ("OnReadHandle: {0} off={1}", file, offset);
            bytesWritten = -1;
            Stream strm = GetStream (info.Handle);
            if (strm == null || strm.Position != offset)
                return Errno.EIO;
            if (!strm.CanRead)
                return Errno.EINVAL;
            bytesWritten = strm.Read (buf, 0, buf.Length);
            return 0;
        }

        protected override Errno OnCreateHandle (string file, OpenedPathInfo info, FilePermissions mode)
        {
            Console.WriteLine ("OnCreateHandle: {0}", file);
            Stream strm = _fs.WriteOpen (file);
            if (strm == null)
                return Errno.EIO;
            info.Handle = RegisterState (strm);
            return 0;
        }

        protected override Errno OnWriteHandle (string file, OpenedPathInfo info, byte[] buf, long offset, out int bytesRead)
        {
            Console.WriteLine ("OnWriteHandle: {0} off={1}", file, offset);
            bytesRead = -1;
            Stream strm = GetStream (info.Handle);
            if (strm == null || strm.Position != offset)
                return Errno.EIO;
            if (!strm.CanWrite)
                return Errno.EINVAL;
            strm.Write (buf, 0, buf.Length);
            bytesRead = buf.Length;
            return 0;
        }

        protected override Errno OnFlushHandle (string file, OpenedPathInfo info)
        {
            Console.WriteLine ("OnFlushHandle: {0}", file);
            Stream strm = GetStream (info.Handle);
            if (strm == null)
                return Errno.EIO;
            if (strm.CanWrite)
                strm.Flush ();
            return 0;
        }

        protected override Errno OnReleaseHandle (string file, OpenedPathInfo info)
        {
            Console.WriteLine ("OnReleaseHandle: {0}", file);
            object obj;
            using (var l = _lock.WriteLock ()) {
                if (!_handles.TryGetValue (info.Handle.ToInt32 (), out obj))
                    return Errno.EINVAL;
                _handles.Remove (info.Handle.ToInt32 ());
            }
            Stream strm = obj as Stream;
            if (strm != null) {
                strm.Close ();
                return 0;
            }
            return Errno.EIO;
        }

        protected override Errno OnCreateDirectory (string directory, FilePermissions mode)
        {
            Console.WriteLine ("OnCreateDirectory: {0}", directory);
            if (_fs.CreateDirectory (directory))
                return 0;
            return Errno.EIO;
        }

        protected override Errno OnRemoveDirectory (string directory)
        {
            Console.WriteLine ("OnRemoveDirectory: {0}", directory);
            if (_fs.DeleteDirectory (directory))
                return 0;
            return Errno.EIO;
        }

        protected override Errno OnRemoveFile (string file)
        {
            Console.WriteLine ("OnRemoveFile: {0}", file);
            if (_fs.DeleteFile (file))
                return 0;
            return Errno.EIO;
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

        #if false
        protected override Errno OnAccessPath (string path, AccessModes mode)
        {
            Console.WriteLine ("OnAccessPath: {0}", path);
            return base.OnAccessPath (path, mode);
        }

        protected override Errno OnGetFileSystemStatus (string path, out Statvfs buf)
        {
            Console.WriteLine ("OnGetFileSystemStatus: {0}", path);
            return base.OnGetFileSystemStatus (path, out buf);
        }

        protected override Errno OnGetHandleStatus (string file, OpenedPathInfo info, out Stat buf)
        {
            Console.WriteLine ("OnGetHandleStatus: {0}", file);
            return base.OnGetHandleStatus (file, info, out buf);
        }
        #endif
    }
}
