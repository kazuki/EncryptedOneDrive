using System;
using System.Collections.Generic;
using Mono.Fuse;
using Mono.Unix.Native;

namespace EncryptedOneDrive
{
    public class Fuse : Mono.Fuse.FileSystem
    {
        EncryptedOneDrive.FileSystem _fs;

        public Fuse (EncryptedOneDrive.FileSystem fs)
        {
            _fs = fs;
        }

        protected override Errno OnGetPathStatus (string path, out Stat stat)
        {
            Console.WriteLine ("OnGetPathStatus: {0}", path);
            stat = new Stat ();
            var x = _fs.Stat (path);
            if (x == null)
                return Errno.ENOENT;
            if (x.IsFile) {
                FileSystem.FileEntry e = (FileSystem.FileEntry)x;
                stat.st_mode = FilePermissions.S_IFREG | NativeConvert.FromOctalPermissionString ("0444");
                stat.st_nlink = 1;
                stat.st_size = e.Size;
            } else {
                stat.st_mode = FilePermissions.S_IFDIR | NativeConvert.FromOctalPermissionString ("0755");
                stat.st_nlink = 2;
            }
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
                list.Add (new DirectoryEntry (x [i].Name));
            }
            return 0;
        }

        protected override Errno OnOpenHandle (string file, OpenedPathInfo info)
        {
            if (info.OpenAccess.HasFlag (OpenFlags.O_RDONLY)) {
                // Read-mode (not implemented)
                return Errno.EACCES;
            } else if (info.OpenAccess.HasFlag (OpenFlags.O_WRONLY)) {
                // Write-mode
                Console.WriteLine (info.Handle);
                return Errno.EIO;
            } else {
                // Read-Write-mode (not supported)
                return Errno.EACCES;
            }
        }
    }
}
