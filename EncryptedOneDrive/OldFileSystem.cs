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
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace EncryptedOneDrive
{
    public class OldFileSystem : IDisposable
    {
        const string BASE_PATH = "/encrypted";
        const string META_PATH = BASE_PATH + "/meta";
        const string DATA_PATH = BASE_PATH + "/data";
        static readonly TimeSpan LogRotateInterval = TimeSpan.FromMinutes (10);
        const int MaxLogEntry = 100;

        DirectoryEntry _root = new DirectoryEntry (string.Empty, null, DateTime.UtcNow);
        readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(); // TODO: ロックを細かくしたい...

        long _logVersion = 0;
        readonly Thread _logUploader;
        readonly AutoResetEvent _logRotateSignal = new AutoResetEvent (false);
        readonly List<LogEntry> _log = new List<LogEntry> ();

        CryptoManager _cryptoMgr;

        public OldFileSystem (OldOneDriveClient client, CryptoManager cryptoMgr)
        {
            this.Client = client;
            _cryptoMgr = cryptoMgr;

            Console.WriteLine ("initializing...");
            // update cache
            client.GetFiles (BASE_PATH);
            client.CreateDirectory (META_PATH);
            client.CreateDirectory (DATA_PATH);
            client.GetFiles (DATA_PATH);
            for (int i = 0; i < 256; ++i) {
                string path = DATA_PATH + "/" + i.ToString ("x2");
                if (client.CreateDirectory (path))
                    Console.WriteLine ("  created data directory: {0}", path);
            }

            var meta_entries = Client.GetFiles (META_PATH);
            using (var l = _lock.WriteLock ()) {
                Console.WriteLine ("  reading checkpoint...(not implemented)");
                Console.WriteLine ("  reading log...(not implemented)");
                LogReplay (meta_entries);
            }
            Console.WriteLine ("done");

            _logUploader = new Thread (LogUploadThread);
            _logUploader.Start ();
        }

        public void Close ()
        {
            if (_root == null)
                return;
            _root = null;
            _logRotateSignal.Set ();
            _logUploader.Join ();
            _logRotateSignal.Close ();
        }

        public void Dispose ()
        {
            Close ();
        }

        public Entry Stat (string path)
        {
            if (!path.StartsWith ("/", StringComparison.InvariantCulture))
                throw new ArgumentException ();
            using (var l = _lock.ReadLock ()) {
                return Lookup (path);
            }
        }

        public Entry[] List (string path)
        {
            if (!path.StartsWith ("/", StringComparison.InvariantCulture))
                throw new ArgumentException ();
            using (var l = _lock.ReadLock ()) {
                Entry dir = Lookup (path);
                if (dir == null)
                    return null;
                if (dir.IsFile)
                    return null;
                return ((DirectoryEntry)dir).Children.ToArray ();
            }
        }

        public bool CreateDirectory (string path)
        {
            DateTime creationTime = DateTime.UtcNow;
            using (var l = _lock.WriteLock ()) {
                if (!CreateDirectoryInternal (path, creationTime))
                    return false;
                WriteLog (LogType.CreateDirectory, path, creationTime, null, 0);
            }
            return true;
        }

        bool CreateDirectoryInternal (string path, DateTime creationTime)
        {
            string name;
            string parentPath = GetParentPath (path, out name);
            if (name == null)
                return false;
            DirectoryEntry parentEntry = Lookup (parentPath) as DirectoryEntry;
            if (parentEntry == null)
                return false;
            var newEntry = new DirectoryEntry (name, parentEntry, creationTime);
            parentEntry.Children.Add (newEntry);
            return true;
        }

        public Stream ReadOpen (string path)
        {
            FileEntry entry;
            using (var l = _lock.ReadLock()) {
                entry = Lookup (path) as FileEntry;
                if (entry == null)
                    return null;
            }
            return new FileReader (this, entry);
        }

        public Stream WriteOpen (string path)
        {
            DateTime creationTime = DateTime.UtcNow;
            FileEntry newEntry;
            using (var l = _lock.WriteLock()) {
                if (!CreateFileInternal (path, null, 0, creationTime, out newEntry))
                    return null;
            }
            return new FileWriter (path, this, newEntry);
        }

        bool CreateFileInternal (string path, SegmentID[] segments, long fileSize, DateTime creationTime, out FileEntry newEntry)
        {
            newEntry = null;
            string name;
            string parentPath = GetParentPath (path, out name);
            if (name == null)
                return false;
            DirectoryEntry parentEntry = Lookup (parentPath) as DirectoryEntry;
            if (parentEntry == null)
                return false;
            newEntry = new FileEntry (name, parentEntry, creationTime, fileSize, segments);
            if (parentEntry.Lookup (name) != null)
                return false;
            parentEntry.Children.Add (newEntry);
            return true;
        }

        public bool DeleteFile (string path)
        {
            FileEntry entry;
            using (var l = _lock.WriteLock ()) {
                if (!DeleteFileInternal (path, out entry))
                    return false;
                WriteLog (LogType.DeleteFile, path, DateTime.UtcNow, null, 0);
            }

            // delete segments
            for (int i = 0; i < entry.Segments.Length; ++i) {
                string tag = entry.Segments [i].ToString ();
                string segment_path = DATA_PATH + "/" + tag.Substring (0, 2) + "/" + tag.Substring (2);
                Client.Delete (segment_path);
            }

            return true;
        }

        bool DeleteFileInternal (string path, out FileEntry entry)
        {
            entry = Lookup (path) as FileEntry;
            if (entry == null)
                return false;
            entry.Parent.Children.Remove (entry);
            return true;
        }

        public bool DeleteDirectory (string path)
        {
            using (var l = _lock.WriteLock ()) {
                if (!DeleteDirectoryInternal (path))
                    return false;
                WriteLog (LogType.DeleteDirectory, path, DateTime.UtcNow, null, 0);
            }
            return true;
        }

        bool DeleteDirectoryInternal (string path)
        {
            DirectoryEntry entry = Lookup (path) as DirectoryEntry;
            if (entry == null || entry.Parent == null)
                return false;
            if (entry.Children.Count > 0)
                return false;
            entry.Parent.Children.Remove (entry);
            return true;
        }

        string GetParentPath (string path)
        {
            string fn;
            return GetParentPath (path, out fn);
        }

        string GetParentPath (string path, out string filename)
        {
            int pos = path.LastIndexOf ("/", StringComparison.InvariantCulture);
            filename = path.Substring (pos + 1);
            if (filename.Length == 0)
                filename = null;
            string parentPath = path.Substring (0, pos);
            if (parentPath.Length == 0)
                return "/";
            return parentPath;
        }

        Entry Lookup (string path)
        {
            if (path.Length == 1 && path [0] == '/')
                return _root;
            string[] items = path.Split ('/');
            DirectoryEntry e = _root;
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
            return null;
        }

        void LogReplay (OneDriveEntry[] entries)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer (typeof(LogEntry[]));
            var validLogs = new SortedDictionary<long, OneDriveEntry> ();
            foreach (OneDriveEntry entry in entries) {
                if (!entry.Name.StartsWith ("log.", StringComparison.InvariantCulture))
                    continue;
                long logVer;
                int pos = entry.Name.IndexOf ('-');
                if (pos < 0)
                    continue;
                if (!long.TryParse (entry.Name.Substring (4, pos - 4), NumberStyles.HexNumber, null, out logVer))
                    continue;
                if (logVer < _logVersion)
                    continue;
                validLogs.Add (logVer, entry);
            }
            if (validLogs.Count == 0)
                return;
            foreach (var kv in validLogs) {
                byte[] tag = Utility.ParseHexString (kv.Value.Name.Substring (kv.Value.Name.IndexOf ('-') + 1));
                string path = META_PATH + "/" + kv.Value.Name;
                LogEntry[] logs;
                using (MemoryStream ms = new MemoryStream (Client.DownloadBytes (path)))
                using (Stream strm = _cryptoMgr.WrapInDecryptor (ms, tag)) {
                    logs = (LogEntry[])serializer.ReadObject (strm);
                }
                for (int i = 0; i < logs.Length; ++i)
                    LogReplay (logs [i]);
                _logVersion = kv.Key;
            }
            ++_logVersion;
        }

        void LogReplay (LogEntry log)
        {
            switch (log.Type) {
            case LogType.CreateDirectory:
                CreateDirectoryInternal (log.Path, new DateTime (log.UtcTicks, DateTimeKind.Utc));
                break;
            case LogType.DeleteDirectory:
                DeleteDirectoryInternal (log.Path);
                break;
            case LogType.CreateFile:
                SegmentID[] segments = new SegmentID[log.Segments == null ? 0 : log.Segments.Length];
                for (int i = 0; i < segments.Length; ++i)
                    segments [i] = new SegmentID (log.Segments [i]);
                FileEntry newEntry;
                CreateFileInternal (log.Path, segments, log.Size, new DateTime (log.UtcTicks, DateTimeKind.Utc), out newEntry);
                break;
            case LogType.DeleteFile:
                FileEntry entry;
                DeleteFileInternal (log.Path, out entry);
                break;
            default:
                throw new FormatException ();
            }
        }

        void WriteLog (LogType type, string path, DateTime utc, string[] segments, long size)
        {
            LogEntry e = new LogEntry {
                Type = type,
                Path = path,
                UtcTicks = utc.Ticks,
                Segments = segments,
                Size = size
            };
            lock (_log) {
                _log.Add (e);
                if (_log.Count > MaxLogEntry)
                    _logRotateSignal.Set ();
            }
        }

        void LogUploadThread ()
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer (typeof(LogEntry[]));
            while (_root != null) {
                _logRotateSignal.WaitOne (LogRotateInterval);

                LogEntry[] logs;
                lock (_log) {
                    logs = _log.ToArray ();
                    _log.Clear ();
                }

                if (logs.Length == 0)
                    continue;
                byte[] raw;
                byte[] tag;
                using (MemoryStream ms = new MemoryStream())
                using (Stream strm = _cryptoMgr.WrapInEncryptor(ms, out tag)) {
                    serializer.WriteObject (strm, logs);
                    strm.Close ();
                    raw = ms.ToArray ();
                }

                string path = META_PATH + "/log." + _logVersion.ToString ("x16") + "-" + tag.ToHexString ();
                try {
                    Client.Upload (path, raw);
                    ++_logVersion;
                } catch {
                    Client.Delete (path);
                    lock (_log) {
                        _log.InsertRange (0, logs);
                        if (_log.Count > MaxLogEntry)
                            _logRotateSignal.Set ();
                    }
                }
            }
        }

        OldOneDriveClient Client { get; set; }

        internal struct SegmentID
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
        }

        public abstract class Entry
        {
            protected Entry(string name, DirectoryEntry parent, bool isFile, DateTime utc)
            {
                this.Name = name;
                this.Parent = parent;
                this.CreationTimeUtc = utc;
                this.LastWriteTimeUtc = utc;
                this.IsFile = isFile;
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
            public DirectoryEntry(string name, DirectoryEntry parent, DateTime creationTime) : base (name, parent, false, creationTime)
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
            static readonly SegmentID[] EmptySegments = new SegmentID[0];

            public FileEntry(string name, DirectoryEntry parent, DateTime creationTime, long size) : this(name, parent, creationTime, size, null)
            {
            }

            internal FileEntry(string name, DirectoryEntry parent, DateTime creationTime, long size, SegmentID[] segments) : base(name, parent, true, creationTime)
            {
                this.Size = size;
                if (segments == null || segments.Length == 0) {
                    this.Segments = EmptySegments;
                } else {
                    this.Segments = segments;
                }
            }

            internal void AddSegment (SegmentID id, long newSize)
            {
                lock (this) {
                    var old = this.Segments;
                    SegmentID[] list = new SegmentID[old.Length + 1];
                    Array.Copy (old, 0, list, 0, old.Length);
                    list [old.Length] = id;

                    this.Size = newSize;
                    this.Segments = list;
                }
            }

            public long Size { get; private set; }
            internal SegmentID[] Segments { get; private set; }
        }

        class FileReader : Stream
        {
            readonly OldFileSystem _fs;
            readonly FileEntry _entry;
            int _currentSegmentIdx = -1;
            long _pos = 0;
            Stream _strm = null;

            public FileReader (OldFileSystem fs, FileEntry entry)
            {
                _fs = fs;
                _entry = entry;
            }

            public override int Read (byte[] buffer, int offset, int count)
            {
                while (true) {
                    if (_strm == null) {
                        if (!OpenNextSegment ())
                            return 0; // EOF
                    }

                    int size = _strm.Read (buffer, offset, count);
                    if (size <= 0) {
                        _strm.Close ();
                        _strm = null;
                        if (size < 0)
                            throw new IOException ();
                    } else {
                        _pos += size;
                        return size;
                    }
                }
            }

            bool OpenNextSegment ()
            {
                if (_strm != null) {
                    _strm.Close ();
                    _strm = null;
                }

                ++_currentSegmentIdx;
                if (_currentSegmentIdx == _entry.Segments.Length)
                    return false;

                string tag = _entry.Segments [_currentSegmentIdx].ToString ();
                string path = DATA_PATH + "/" + tag.Substring (0, 2) + "/" + tag.Substring (2);
                _strm = _fs._cryptoMgr.WrapInDecryptor (_fs.Client.Download (path), Utility.ParseHexString (tag));
                return true;
            }

            public override void Flush ()
            {
                throw new NotSupportedException ();
            }
            public override long Seek (long offset, SeekOrigin origin)
            {
                throw new NotSupportedException ();
            }
            public override void SetLength (long value)
            {
                throw new NotSupportedException ();
            }
            public override void Write (byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException ();
            }
            public override bool CanRead {
                get { return true; }
            }
            public override bool CanSeek {
                get { return false; }
            }
            public override bool CanWrite {
                get { return false; }
            }
            public override long Length {
                get { return _entry.Size; }
            }
            public override long Position {
                get { return _pos; }
                set { throw new NotSupportedException (); }
            }
        }

        class FileWriter : Stream
        {
            readonly OldFileSystem _fs;
            readonly OldOneDriveClient _client;
            readonly FileEntry _entry;
            readonly CryptoManager _crypto;
            readonly string _path;
            readonly DateTime _creationTime;
            long _size = 0;
            bool _closed = false;
            int _writtenBytes;
            readonly int _maxWrittenSize;
            readonly int _maxSegmentSize;
            byte[] _buf;
            Stream _strm;
            byte[] _tag;

            readonly Thread _uploadThread;
            Exception _uploadException = null;
            readonly AutoResetEvent _uploadSignal = new AutoResetEvent (false);
            readonly AutoResetEvent _uploadDoneSignal = new AutoResetEvent (false);
            UploadInfo _uploadInfo = null;

            public FileWriter (string path, OldFileSystem fs, FileEntry entry)
            {
                _fs = fs;
                _client = fs.Client;
                _entry = entry;
                _crypto = fs._cryptoMgr;
                _path = path;
                _creationTime = DateTime.UtcNow;
                _maxSegmentSize = 1024 * 1024 * 64;
                _maxWrittenSize = _maxSegmentSize - _crypto.AuthenticatedEncryption.IVByteSize;
                InitSegment();
                _uploadThread = new Thread (UploadThread);
                _uploadThread.Start();
            }

            public override void Write (byte[] buffer, int offset, int count)
            {
                while (count > 0) {
                    int size = Math.Min (count, _maxWrittenSize - _writtenBytes);
                    _strm.Write (buffer, offset, size);
                    _writtenBytes += size;
                    _size += size;
                    offset += size;
                    count -= size;
                    if (_writtenBytes == _maxWrittenSize)
                        Flush ();
                }
            }

            void InitSegment ()
            {
                _buf = new byte[_maxSegmentSize];
                _strm = _crypto.WrapInEncryptor (new MemoryStream (_buf), out _tag);
                _writtenBytes = 0;
            }

            public override void Flush ()
            {
                while (_uploadInfo != null)
                    _uploadDoneSignal.WaitOne ();
                if (_writtenBytes == 0)
                    return;
                _strm.Close ();
                if (_uploadException != null)
                    throw _uploadException;
                string tag = _tag.ToHexString ();
                _uploadInfo = new UploadInfo {
                    Buffer = _buf,
                    Offset = 0,
                    Count = _writtenBytes + _crypto.AuthenticatedEncryption.IVByteSize,
                    Path = DATA_PATH + "/" + tag.Substring (0, 2) + "/" + tag.Substring (2),
                    NewFileSize = _size,
                    ID = new SegmentID (_tag)
                };
                InitSegment ();
                _uploadSignal.Set ();
            }

            public override void Close ()
            {
                base.Close ();
                if (_closed)
                    return;
                try {
                    Flush ();
                } catch {}
                _closed = true;
                _uploadSignal.Set ();
                _uploadThread.Join ();
                _uploadSignal.Close ();
                if (_uploadException == null) {
                    SegmentID[] segments = _entry.Segments;
                    string[] list = new string[segments.Length];
                    for (int i = 0; i < segments.Length; ++i)
                        list [i] = segments [i].ToString ();
                    _fs.WriteLog (LogType.CreateFile, _path, _creationTime, list, _size);
                } else {
                    throw _uploadException;
                }
            }

            void UploadThread()
            {
                while (!_closed) {
                    if (!_uploadSignal.WaitOne () || _uploadInfo == null)
                        continue;

                    UploadInfo info = _uploadInfo;
                    try {
                        _client.Upload (info.Path, info.Buffer, info.Offset, info.Count);
                        _entry.AddSegment (info.ID, info.NewFileSize);
                    } catch (Exception e) {
                        _uploadException = e;
                        return;
                    } finally {
                        _uploadInfo = null;
                        _uploadDoneSignal.Set ();
                    }
                }
            }

            class UploadInfo
            {
                public byte[] Buffer { get; set; }
                public int Offset { get; set; }
                public int Count { get; set; }
                public string Path { get; set; }
                public SegmentID ID { get; set; }
                public long NewFileSize { get; set; }
            }

            public override int Read (byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException ();
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
                get { return false; }
            }
            public override bool CanSeek {
                get { return false; }
            }
            public override bool CanWrite {
                get { return true; }
            }
            public override long Length {
                get { return _size; }
            }
            public override long Position {
                get { return _size; }
                set { throw new NotSupportedException (); }
            }
        }

        [DataContract]
        class LogEntry
        {
            [DataMember]
            public LogType Type;

            [DataMember]
            public string Path;

            [DataMember]
            public long UtcTicks;

            [DataMember]
            public long Size;

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
