using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace EncryptedOneDrive
{
    public class FileSystem
    {
        const string META_PATH = "/encrypted/meta";
        const string DATA_PATH = "/encrypted/data";
        DirectoryEntry _root = new DirectoryEntry (string.Empty, null);
        ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(); // TODO: ロックを細かくしたい...

        public FileSystem (OneDriveClient client)
        {
            this.Client = client;

            Console.WriteLine ("initializing...");
            client.CreateDirectory (META_PATH);
            client.CreateDirectory (DATA_PATH);
            client.GetFiles (DATA_PATH); // update cache
            for (int i = 0; i < 256; ++i) {
                string path = DATA_PATH + "/" + i.ToString ("x2");
                if (client.CreateDirectory (path))
                    Console.WriteLine ("  created data directory: {0}", path);
            }
            Console.WriteLine ("  reading checkpoint...(not implemented)");
            Console.WriteLine ("  reading log...(not implemented)");
            Console.WriteLine ("done");
        }

        public Entry Stat (string path)
        {
            if (!path.StartsWith ("/", StringComparison.InvariantCulture))
                throw new ArgumentException ();
            return Lookup (path);
        }

        public Entry[] List (string path)
        {
            if (!path.StartsWith ("/", StringComparison.InvariantCulture))
                throw new ArgumentException ();
            Entry dir = Lookup (path);
            if (dir == null)
                return null;
            if (dir.IsFile)
                return null;
            return ((DirectoryEntry)dir).Children.ToArray ();
        }

        public Stream ReadOpen (string path)
        {
            Entry e = Stat (path);
            if (e == null || !e.IsFile)
                return null;
            return null;
        }

        public Stream WriteOpen (string path)
        {
            int pos = path.LastIndexOf ("/", StringComparison.InvariantCulture);
            if (pos <= 1)
                return null;
            string name = path.Substring (pos + 1);
            string parentPath = path.Substring (0, pos);
            DirectoryEntry parentEntry = Stat (parentPath) as DirectoryEntry;
            if (parentEntry == null || name.Length == 0)
                return null;

            bool removeFlag = false;
            using (var l = _lock.ReadLock ()) {
                var x = parentEntry.Lookup (name);
                if (x != null) {
                    if (x.IsDirectory)
                        return null;
                    removeFlag = true;
                }
            }
            if (removeFlag)
                DeleteFile (path);

            FileEntry newEntry = new FileEntry (name, parentEntry, 0);
            using (var l = _lock.WriteLock()) {
                var x = parentEntry.Lookup (name);
                if (x != null)
                    return null;
                parentEntry.Children.Add (newEntry);
            }
            return new FileWriter (newEntry);
        }

        public bool DeleteFile (string path)
        {
            return false;
        }

        public bool DeleteDirectory (string path)
        {
            return false;
        }

        Entry Lookup (string path)
        {
            if (path.Length == 1 && path [0] == '/')
                return _root;
            string[] items = path.Split ('/');
            DirectoryEntry e = _root;
            using (var l = _lock.ReadLock ()) {
                for (int i = 1; i < items.Length; ++i) {
                    var x = e.Lookup (items [i]);
                    if (x == null)
                        return null;
                    if (i == items.Length - 1)
                        return x;
                    if (x.IsFile)
                        return null;
                    e = (DirectoryEntry)x;
                }
            }
            return null;
        }

        OneDriveClient Client { get; set; }

        struct SegmentID : IComparable<SegmentID>, IEquatable<SegmentID>
        {
            readonly ulong _0, _1, _2, _3;

            public SegmentID(byte[] data)
            {
                if (data.Length != 32)
                    throw new ArgumentException();
                _0 = ToUint64BE(data, 0);
                _1 = ToUint64BE(data, 8);
                _2 = ToUint64BE(data, 16);
                _3 = ToUint64BE(data, 24);
            }

            public SegmentID (string txt)
            {
                if (txt.Length != 64)
                    throw new ArgumentException();
                byte[] tmp = new byte[32];
                for (int i = 0; i < txt.Length; i += 2) {
                    tmp[i / 2] = byte.Parse(txt.Substring(i, 2), NumberStyles.HexNumber);
                }
                _0 = ToUint64BE(tmp, 0);
                _1 = ToUint64BE(tmp, 8);
                _2 = ToUint64BE(tmp, 16);
                _3 = ToUint64BE(tmp, 24);
            }

            public override string ToString ()
            {
                StringBuilder sb = new StringBuilder (64);
                sb.AppendFormat ("{0:x16}", _0);
                sb.AppendFormat ("{0:x16}", _1);
                sb.AppendFormat ("{0:x16}", _2);
                sb.AppendFormat ("{0:x16}", _3);
                return sb.ToString ();
            }

            static ulong ToUint64BE(byte[] x, int i)
            {
                return ((ulong)x [i + 0] << 56) | ((ulong)x [i + 1] << 48) | ((ulong)x [i + 2] << 40) |
                    ((ulong)x [i + 3] << 32) | ((ulong)x [i + 4] << 24) | ((ulong)x [i + 5] << 16) |
                    ((ulong)x [i + 6] << 8) | (ulong)x [i + 7];
            }

            #region IEquatable/IComparable implementation

            public override bool Equals (object obj)
            {
                return this.Equals ((SegmentID)obj);
            }

            public bool Equals (SegmentID other)
            {
                return this._0 == other._0 && this._1 == other._1 &&
                    this._2 == other._2 && this._3 == other._3;
            }

            public override int GetHashCode ()
            {
                return _0.GetHashCode () ^ _1.GetHashCode () ^ _2.GetHashCode () ^ _3.GetHashCode ();
            }

            public int CompareTo (SegmentID other)
            {
                int ret;
                ret = _0.CompareTo (other._0);
                if (ret != 0)
                    return ret;
                ret = _1.CompareTo (other._1);
                if (ret != 0)
                    return ret;
                ret = _2.CompareTo (other._2);
                if (ret != 0)
                    return ret;
                return _3.CompareTo (other._3);
            }

            #endregion
        }

        public class Entry
        {
            protected Entry(string name, DirectoryEntry parent, bool isFile)
            {
                this.Name = name;
                this.Parent = parent;
                this.CreationTimeUtc = DateTime.UtcNow;
            }

            public DirectoryEntry Parent { get; private set; }
            public string Name { get; set; }
            public DateTime CreationTimeUtc { get; private set; }
            public DateTime LastWriteTimeUtc { get; set; }
            public bool IsFile { get; private set; }
            public bool IsDirectory { get { return !IsFile; } }
        }

        public class DirectoryEntry : Entry
        {
            public DirectoryEntry(string name, DirectoryEntry parent) : base (name, parent, false)
            {
                this.Children = new List<Entry> (0);
            }

            public List<Entry> Children { get; private set; }

            public Entry Lookup (string name)
            {
                for (int i = 0; i < Children.Count; ++i) {
                    if (name == Children [i].Name)
                        return Children [i];
                }
                return null;
            }
        }

        public class FileEntry : Entry
        {
            public FileEntry(string name, DirectoryEntry parent, long size) : base(name, parent, true)
            {
                this.Size = size;
            }

            public long Size { get; private set; }
        }

        class FileWriter : Stream
        {
            readonly FileEntry _entry;
            public FileWriter (FileEntry entry)
            {
                _entry = entry;
            }

            #region implemented abstract members of Stream
            public override void Flush ()
            {
                throw new NotImplementedException ();
            }
            public override int Read (byte[] buffer, int offset, int count)
            {
                throw new NotImplementedException ();
            }
            public override long Seek (long offset, SeekOrigin origin)
            {
                throw new NotImplementedException ();
            }
            public override void SetLength (long value)
            {
                throw new NotImplementedException ();
            }
            public override void Write (byte[] buffer, int offset, int count)
            {
                throw new NotImplementedException ();
            }
            public override bool CanRead {
                get {
                    throw new NotImplementedException ();
                }
            }
            public override bool CanSeek {
                get {
                    throw new NotImplementedException ();
                }
            }
            public override bool CanWrite {
                get {
                    throw new NotImplementedException ();
                }
            }
            public override long Length {
                get {
                    throw new NotImplementedException ();
                }
            }
            public override long Position {
                get {
                    throw new NotImplementedException ();
                }
                set {
                    throw new NotImplementedException ();
                }
            }
            #endregion
        }

        [DataContract]
        class LogEntry
        {
            [DataMember]
            public LogType Type;

            [DataMember]
            public string Path;

            [DataMember]
            public string[] Segments;
        }

        enum LogType
        {
            CreateFile,
            CreateDirectory,
            DeleteFile,
            DeleteDirectory
        }
    }
}
