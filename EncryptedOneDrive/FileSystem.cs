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
// along withap this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Text;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Xml;

namespace EncryptedOneDrive
{
    public class FileSystem : FileSystemBase
    {
        #region variables
        const byte MetaFileVersion = 0; // チェックポイント/ログファイルのバージョン
        const int DirPrefixLength = 3;

        readonly FileSystemBase fs;
        readonly CryptoManager crypto;
        readonly DirectoryEntry root = new DirectoryEntry ("", DateTime.MinValue, null);
        readonly ReaderWriterLockSlim rwlock = new ReaderWriterLockSlim ();
        readonly string fsRootPath;
        readonly string fsMetaPath;
        readonly string fsDataPath;
        readonly int MaxSegmentSize;

        readonly string logDir;
        ulong logVersion = 0; // 現在オープンしているログのバージョン
        string logPath = null; // 現在オープンしているログのローカルファイルパス
        BinaryWriter logWriter = null; // logPathのBinaryWriter
        byte[] logTag = null; // logWriterクローズ時に書き込まれる認証タグ情報
        #endregion

        public FileSystem (Config cfg, FileSystemBase baseFS, CryptoManager crypto)
        {
            if (baseFS == null || crypto == null)
                throw new ArgumentNullException ();
            this.fs = baseFS;
            this.crypto = crypto;
            this.logDir = cfg.ApplicationDataDirectory;
            this.fsRootPath = cfg.Get ("fs.root", "/encrypted-overlay-filesystem");
            this.fsMetaPath = Utility.CombinePath (this.fsRootPath, "meta");
            this.fsDataPath = Utility.CombinePath (this.fsRootPath, "data");
            this.fs.CreateDirectory (this.fsMetaPath);
            var maxSegSize = cfg.Get ("fs.max-segment-size", 1024 * 1024 * 64);
            if (maxSegSize <= 0 || maxSegSize > int.MaxValue)
                throw new ArgumentException ();
            this.MaxSegmentSize = (int)maxSegSize;

            using (var wrlock = rwlock.WriteLock ()) {
                LoadMetadata ();
            }
        }

        public override void Dispose ()
        {
            if (logWriter != null) {
                using (var wlock = rwlock.WriteLock ()) {
                    FlushLog (true, true);
                }
                logWriter = null;
            }
            fs.Dispose ();
            rwlock.Dispose ();
        }

        #region implemented abstract members of FileSystemBase

        public override FileProperty Stat (string path)
        {
            FileProperty prop;
            ValidatePath (path);
            using (var rdlock = rwlock.ReadLock ()) {
                prop = Lookup (path);
            }
            if (prop == null)
                throw new FileNotFoundException ();
            return prop;
        }

        public override FileProperty[] List (string path)
        {
            Entry prop;
            ValidatePath (path);
            using (var rdlock = rwlock.ReadLock ()) {
                prop = Lookup (path);
            }
            if (prop == null)
                throw new FileNotFoundException ();
            var dirProp = prop as DirectoryEntry;
            if (dirProp == null)
                throw new IOException ();
            return dirProp.Children;
        }

        public override Stream ReadOpen (string path, out FileProperty stat)
        {
            Entry prop;
            using (var rdlock = rwlock.ReadLock ()) {
                prop = Lookup (path);
            }
            if (prop == null)
                throw new FileNotFoundException ();
            var fileProp = prop as FileEntry;
            if (fileProp == null)
                throw new IOException ();

            stat = fileProp;
            return new Reader (this, fileProp);
        }

        public override void GetStorageUsage (out long totalSize, out long availableSize)
        {
            fs.GetStorageUsage (out totalSize, out availableSize);
        }

        public override void Delete (string path)
        {
            ValidatePath (path);
            var log = new OpLog (OpLogType.Delete, path, DateTime.UtcNow, null);
            using (var wrlock = rwlock.WriteLock ()) {
                AppendLog (log);
                ReplayDeleteFile (path);
            }
        }

        public override FileProperty CreateDirectory (string path)
        {
            ValidatePath (path);
            var log = new OpLog (OpLogType.CreateDirectory, path, DateTime.UtcNow, null);
            using (var wrlock = rwlock.WriteLock ()) {
                AppendLog (log);
                return ReplayCreateDirectory (path, log.Time);
            }
        }

        public override Stream WriteOpen (string path)
        {
            ValidatePath (path);
            var log = new OpLog (OpLogType.CreateFile, path, DateTime.UtcNow, null);
            using (var wrlock = rwlock.WriteLock ()) {
                AppendLog (log);
                ReplayCreateFile (path, log.Time);
            }
            return new Writer (this, path);
        }

        public override void WriteAll (string path, Stream strm)
        {
            using (var outStrm = WriteOpen (path)) {
                byte[] buf = new byte[8192];
                while (true) {
                    int size = strm.Read (buf, 0, buf.Length);
                    if (size == 0)
                        return;
                    if (size < 0)
                        throw new IOException();
                    outStrm.Write (buf, 0, size);
                }
            }
        }

        #endregion

        #region helpers

        void AppendSegment (string path, byte[] seg, uint size)
        {
            var segId = new SegmentID (seg);
            var log = new OpLog (OpLogType.AppendBlock, path, DateTime.UtcNow, new SegmentInfo[] {
                new SegmentInfo (segId, size)
            });
            using (var wrlock = rwlock.WriteLock ()) {
                AppendLog (log);
                ReplayAppendBlock (path, log.Time, log.Segments);
            }
        }

        string GetSegmentPath (string segId)
        {
            string tmp;
            return GetSegmentPath (segId, out tmp);
        }

        string GetSegmentPath (string segId, out string dirPath)
        {
            dirPath = Utility.CombinePath (fsDataPath, segId.Substring (0, DirPrefixLength));
            return Utility.CombinePath (dirPath, segId.Substring (DirPrefixLength));
        }

        #endregion

        #region Reader
        class Reader : Stream
        {
            FileSystem _owner;
            FileEntry _entry;
            long _pos = 0;
            long _segOff = 0;
            byte[] _seg;

            public Reader (FileSystem owner, FileEntry entry)
            {
                _owner = owner;
                _entry = entry;
                _seg = null;
            }

            #region implemented abstract members of Stream
            public override int Read (byte[] buffer, int offset, int count)
            {
                int total = 0;
                while (count > 0) {
                    if (_seg != null && _segOff <= _pos) {
                        int size = (int)Math.Min (count, _segOff + _seg.Length - _pos);
                        if (size > 0) {
                            Buffer.BlockCopy (_seg, (int)(_pos - _segOff), buffer, offset, size);
                            _pos += size;
                            count -= size;
                            total += size;
                            if (count == 0) {
                                return total;
                            }
                            offset += size;
                        }
                    }
                    _seg = null;

                    SegmentInfo[] segs = _entry.Segments;
                    int segIdx = -1;
                    {
                        long segOff = 0;
                        for (var i = 0; i < segs.Length; ++i) {
                            if (segOff <= _pos && _pos < segOff + segs[i].Size) {
                                segIdx = i;
                                _segOff = segOff;
                                break;
                            }
                            segOff += segs[i].Size;
                        }
                    }
                    if (segIdx < 0) {
                        return total;
                    }

                    string tagStr = segs[segIdx].ID.ToString ();
                    var tag = Utility.ParseHexString (tagStr);
                    string path = _owner.GetSegmentPath (tagStr);
                    using (var baseStrm = _owner.fs.ReadOpen (path))
                    using (var strm = _owner.crypto.WrapInDecryptor (baseStrm, tag)) {
                        byte[] tmp = new byte[segs[segIdx].Size];
                        strm.ReadFull (tmp, 0, tmp.Length);
                        strm.Close ();
                        _seg = tmp;
                    }
                }

                return total;
            }

            public override long Seek (long offset, SeekOrigin origin)
            {
                if (origin == SeekOrigin.Current) {
                    offset = _pos + offset;
                } else if (origin == SeekOrigin.End) {
                    offset = _entry.Size + offset;
                }
                if (offset < 0 || offset > _entry.Size)
                    throw new ArgumentOutOfRangeException ();
                if (_seg != null && (offset < _segOff || _segOff + _seg.Length <= offset))
                    _seg = null;
                _pos = offset;
                return offset;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return _entry.Size; } }
            public override long Position {
                get { return Seek (0, SeekOrigin.Current); }
                set { Seek (value, SeekOrigin.Begin); }
            }
            public override void Flush () {}
            public override void SetLength (long value)
            {
                throw new InvalidOperationException ();
            }
            public override void Write (byte[] buffer, int offset, int count)
            {
                throw new InvalidOperationException ();
            }
            #endregion
        }
        #endregion

        #region Writer
        class Writer : Stream
        {

            long _totalLength = 0;
            bool _closed = false;

            readonly int SegmentOverheadSize;
            readonly int MaxWrittenSize;
            byte[] _buf;
            byte[] _tag = null;
            Stream _strm = null;
            int _writtenBytes;

            public Writer (FileSystem owner, string path)
            {
                this.Owner = owner;
                this.FilePath = path;

                SegmentOverheadSize = owner.crypto.AuthenticatedEncryption.IVByteSize;
                MaxWrittenSize = owner.MaxSegmentSize - SegmentOverheadSize;
                _buf = new byte[owner.MaxSegmentSize];
                InitSegment();
            }

            FileSystem Owner { get; set; }
            string FilePath { get; set; }

            void InitSegment()
            {
                if (_strm != null)
                    throw new InvalidOperationException ();
                _strm = Owner.crypto.WrapInEncryptor (
                    new MemoryStream (_buf),
                    out _tag);
                _writtenBytes = 0;
            }

            void UploadSegment()
            {
                _strm.Close ();
                _strm = null;
                var tag = _tag.ToHexString ();
                string dirPath;
                var path = Owner.GetSegmentPath (tag, out dirPath);
                Owner.AppendSegment (this.FilePath, _tag, (uint)_writtenBytes);
                Owner.fs.CreateDirectory (dirPath);
                Owner.fs.WriteAllBytes (path, _buf, 0, _writtenBytes + SegmentOverheadSize);
            }

            #region implemented abstract members of Stream

            public override void Write (byte[] buffer, int offset, int count)
            {
                while (count > 0) {
                    int size = Math.Min (count, MaxWrittenSize - _writtenBytes);
                    _strm.Write (buffer, offset, size);
                    _writtenBytes += size;
                    _totalLength += size;
                    offset += size;
                    count -= size;
                    if (_writtenBytes == MaxWrittenSize)
                        Flush ();
                }
            }

            public override void Flush ()
            {
                if (_writtenBytes == 0)
                    return;
                UploadSegment ();
                if (!_closed)
                    InitSegment ();
            }

            public override void Close ()
            {
                if (_closed)
                    return;
                _closed = true;
                this.Flush ();
            }

            public override long Length { get { return _totalLength; } }
            public override long Position {
                get { return this.Length; }
                set { throw new NotSupportedException (); }
            }
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite{ get { return true; } }
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
            #endregion
        }
        #endregion

        #region metadata load/save & log writer/player

        /*
         * Memo
         * 
         * + バージョン1から始まる
         * + チェックポイントファイルはチェックポイント化したログと同じバージョンを持つ
         * + ログファイルのファイル名は log.<version>.<tag> 書き込み中のファイルは log.<version>.current となる
         * + チェックポイントファイルのファイル名は meta.<version>.<tag>
         * + 終了時に log.<version>.current は log.<version>.<tag> にリネームする
         * + 起動時に log.<version>.current がある場合は，全エントリを再生し問題なければ採用する
         * + 起動時に log.<version>.* がある場合はチェックポイント化を行う
         */
        void LoadMetadata ()
        {
            var needsCheckPoint = false;
            var remoteFiles = fs.List (fsMetaPath);
            var localMetas = Directory.GetFiles (logDir, "meta.*");
            var localLogs = Directory.GetFiles (logDir, "log.*.*");
            Array.Sort (localMetas);
            Array.Sort (localLogs);
            var removeRemoteFiles = new HashSet <string> ();
            var removeLocalFiles = new HashSet <string> ();

            foreach (var e in remoteFiles)
                removeRemoteFiles.Add (e.Name);
            removeLocalFiles.UnionWith (localMetas);
            removeLocalFiles.UnionWith (localLogs);

            // 有効なチェックポイントファイルを抽出
            string latestMetaFileName = null;
            object latestMetaInfo = null;
            foreach (var e in remoteFiles) {
                if (e.Name.StartsWith ("meta.", StringComparison.Ordinal)) {
                    latestMetaFileName = e.Name;
                    latestMetaInfo = e;
                }
            }
            if (localMetas.Length > 0) {
                string latestLocalMetaFileName = null;
                for (int i = localMetas.Length - 1; i >= 0; --i) {
                    if (localMetas[i].EndsWith (".tmp"))
                        continue;
                    latestLocalMetaFileName = localMetas[i];
                    break;
                }
                if (latestMetaFileName == null || string.Compare (latestMetaFileName, Path.GetFileName (latestLocalMetaFileName), StringComparison.Ordinal) <= 0) {
                    latestMetaFileName = Path.GetFileName (latestLocalMetaFileName);
                    latestMetaInfo = latestLocalMetaFileName;
                }
            }
            if (latestMetaFileName != null) {
                ulong ver;
                string tag;
                if (!ParseLogFileName (latestMetaFileName, out ver, out tag))
                    throw new FormatException ();
                Stream strm;
                string localPath = latestMetaInfo as string;
                if (localPath != null) {
                    strm = new FileStream (localPath, FileMode.Open);
                } else {
                    strm = this.fs.ReadOpen (Utility.CombinePath (this.fsMetaPath, latestMetaFileName));
                }
                try {
                    strm = this.crypto.WrapInDecryptor (strm, Utility.ParseHexString (tag));
                    using (var reader = new XmlTextReader (strm)) {
                        while (reader.Read () && reader.NodeType != XmlNodeType.Element) {}
                        if (reader.NodeType != XmlNodeType.Element || reader.Name != "meta")
                            throw new FormatException();
                        if (int.Parse (reader.GetAttribute ("ver")) != MetaFileVersion)
                            throw new NotSupportedException();
                        ReadCheckPoint (reader.ReadSubtree (), this.root);
                    }
                } finally {
                    strm.Dispose ();
                }
                this.logVersion = ver;
            }

            // 有効なログファイルを抽出
            var validLogs = new SortedDictionary<string, object> ();
            foreach (var e in localLogs)
                validLogs.Add (Path.GetFileName (e), e);
            foreach (var e in remoteFiles) {
                // リモートよりローカルにあるファイルを優先する
                if (e.Name.StartsWith ("log.", StringComparison.Ordinal) && !validLogs.ContainsKey (e.Name))
                    validLogs.Add (e.Name, e);
            }
            foreach (var pair in validLogs) {
                ulong ver;
                string tag;
                if (!ParseLogFileName (pair.Key, out ver, out tag)) {
                    // 不正なファイル名は例外を投げておく
                    throw new FormatException ();
                }
                if (ver <= this.logVersion) {
                    // チェックポイントファイルより古いので無視
                    continue;
                }
                if (ver != this.logVersion + 1) {
                    // ログバージョンが抜けているので例外を投げる
                    throw new FormatException ();
                }
                byte[] tagBytes = null;
                if (tag != "current")
                    tagBytes = Utility.ParseHexString (tag);

                Stream strm;
                if ((pair.Value as string) != null) {
                    strm = new FileStream (pair.Value as string, FileMode.Open);
                } else {
                    strm = this.fs.ReadOpen (Utility.CombinePath (this.fsMetaPath, pair.Key));
                }
                try {
                    strm = this.crypto.WrapInDecryptor (strm, tagBytes);
                    if (strm.ReadByte () != MetaFileVersion)
                        throw new FormatException();
                    var reader = new BinaryReader (strm);
                    OpLog log = new OpLog ();
                    while (strm.Position < strm.Length) {
                        log.ReadFrom (reader);
                        Replay (log);
                    }
                    reader.Close(); // check tag
                    needsCheckPoint = true;
                } catch {
                    // 中途半端なログの時は例外を無視する
                    if (tagBytes != null)
                        throw;
                } finally {
                    strm.Dispose ();
                }
                this.logVersion = ver;
            }

            if (needsCheckPoint) {
                // ログが残っているのでチェックポイント化
                WriteCheckPoint (true);
            } else {
                // 読み込んだメタデータを削除対象から除外
                if ((latestMetaInfo as string) != null) {
                    removeLocalFiles.Remove (latestMetaInfo as string);
                } else if ((latestMetaInfo as FileProperty) != null) {
                    removeRemoteFiles.Remove ((latestMetaInfo as FileProperty).Name);
                }
            }

            // 古いログ・チェックポイントファイルを全て削除
            foreach (var localPath in removeLocalFiles) {
                try {
                    File.Delete (localPath);
                } catch {}
            }
            foreach (var name in removeRemoteFiles) {
                try {
                    this.fs.Delete (Utility.CombinePath (this.fsMetaPath, name));
                } catch {}
            }

            FlushLog (false, true);
        }

        void ReadCheckPoint (XmlReader reader, DirectoryEntry entry)
        {
            reader.MoveToContent ();

            while (reader.Read ()) {
                if (reader.NodeType == XmlNodeType.EndElement)
                    break;
                if (reader.NodeType != XmlNodeType.Element)
                    throw new FormatException ();
                var creation = new DateTime (
                                   long.Parse (reader.GetAttribute ("creation")),
                                   DateTimeKind.Utc);
                string name = reader.GetAttribute ("name");
                Entry child = null;
                if (reader.Name == "file") {
                    var size = long.Parse (reader.GetAttribute ("size"));
                    var segments = new List<SegmentInfo> ();
                    while (reader.Read ()) {
                        if (reader.NodeType == XmlNodeType.EndElement)
                            break;
                        if (reader.NodeType != XmlNodeType.Element || reader.Name != "seg" || !reader.IsEmptyElement)
                            throw new FormatException ();
                        segments.Add (new SegmentInfo (new SegmentID (reader.GetAttribute ("id")),
                                uint.Parse (reader.GetAttribute ("size"))));
                    }
                    child = new FileEntry (name, creation, segments);
                    if (child.Size != size)
                        throw new FormatException ();
                } else if (reader.Name == "dir") {
                    var dir = new DirectoryEntry (name, creation, null);
                    child = dir;
                    ReadCheckPoint (reader.ReadSubtree (), dir);
                } else {
                    throw new FormatException ();
                }
                entry.AddChild (child);
            }
            reader.Close ();
        }

        static bool ParseLogFileName (string name, out ulong ver, out string tag)
        {
            ver = 0;
            tag = null;
            try {
                string[] items = name.Split ('.');
                if (items.Length != 3)
                    return false;
                tag = items[2];
                if (!ulong.TryParse (items[1], NumberStyles.HexNumber, null, out ver))
                    return false;
                return true;
            } catch {
                return false;
            }
        }

        void FlushLog (bool isTerminate, bool isSync)
        {
            ulong oldVer = 0;
            string localPath = null;
            string tag = null;
            if (logWriter != null) {
                logWriter.Close ();
                oldVer = logVersion;
                localPath = this.logPath;
                tag = this.logTag.ToHexString ();
            }
            if (!isTerminate) {
                this.logPath = Path.Combine (logDir, "log." + (++logVersion).ToString ("x16") + ".current");
                Stream strm = new FileStream (this.logPath, FileMode.Create);
                strm = this.crypto.WrapInEncryptor (strm, out this.logTag);
                this.logWriter = new BinaryWriter (strm);
                this.logWriter.Write (MetaFileVersion);
            } else {
                logWriter = null;
            }

            if (localPath == null)
                return;

            if (!isSync)
                throw new NotImplementedException ();

            if (new FileInfo (localPath).Length <= 1 + this.crypto.AuthenticatedEncryption.IVByteSize) {
                File.Delete (localPath);
                return;
            }

            var newFileName = "log." + oldVer.ToString ("x16") + "." + tag;
            var newLocalPath = Path.Combine (Path.GetDirectoryName (localPath), newFileName);
            var remotePath = Utility.CombinePath (this.fsMetaPath, newFileName);
            File.Move (localPath, newLocalPath);
            using (var strm = new FileStream (newLocalPath, FileMode.Open)) {
                this.fs.WriteAll (remotePath, strm);
            }
        }

        void WriteCheckPoint (bool isSync)
        {
            if (!isSync)
                throw new NotImplementedException();

            var baseFileName = "meta." + this.logVersion.ToString ("x16") + ".";
            var tmpFileName = baseFileName + "tmp";
            byte[] tag;
            using (var strm = new FileStream (Path.Combine (logDir, tmpFileName), FileMode.Create))
            using (var enc = this.crypto.WrapInEncryptor (strm, out tag))
            using (var writer = new XmlTextWriter (enc, null)) {
                writer.WriteStartDocument ();
                writer.WriteStartElement ("meta");
                writer.WriteAttributeString ("ver", MetaFileVersion.ToString ());

                foreach (var e in root.Children)
                    WriteCheckPointEntry (writer, e);

                writer.WriteEndElement ();
                writer.WriteEndDocument ();
            }

            var newFileName = baseFileName + tag.ToHexString ();
            var newLocalPath = Path.Combine (logDir, newFileName);
            File.Move (Path.Combine (logDir, tmpFileName), newLocalPath);
            using (var strm = new FileStream (newLocalPath, FileMode.Open)) {
                this.fs.WriteAll (
                    Utility.CombinePath (
                        this.fsMetaPath,
                        newFileName),
                    strm);
            }
        }

        void WriteCheckPointEntry (XmlTextWriter writer, Entry entry)
        {
            FileEntry f = entry as FileEntry;
            DirectoryEntry d = entry as DirectoryEntry;
            writer.WriteStartElement (f == null ? "dir" : "file");

            writer.WriteAttributeString ("creation", entry.CreationTime.Ticks.ToString ());
            writer.WriteAttributeString ("name", entry.Name);

            if (f != null) {
                writer.WriteAttributeString ("size", f.Size.ToString ());
                foreach (var seg in f.Segments) {
                    writer.WriteStartElement ("seg");
                    writer.WriteAttributeString ("id", seg.ID.ToString ());
                    writer.WriteAttributeString ("size", seg.Size.ToString ());
                    writer.WriteEndElement ();
                }
            } else {
                foreach (var child in d.Children)
                    WriteCheckPointEntry (writer, child);
            }

            writer.WriteEndElement ();
        }

        void AppendLog (OpLog log)
        {
            log.WriteTo (logWriter);
            logWriter.Flush ();
        }

        void Replay (OpLog log)
        {
            switch (log.OpType) {
            case OpLogType.CreateFile:
                ReplayCreateFile (log.Path, log.Time);
                break;
            case OpLogType.CreateDirectory:
                ReplayCreateDirectory (log.Path, log.Time);
                break;
            case OpLogType.Delete:
                ReplayDeleteFile (log.Path);
                break;
            case OpLogType.AppendBlock:
                ReplayAppendBlock (log.Path, log.Time, log.Segments);
                break;
            default:
                throw new ArgumentException ();
            }
        }

        FileEntry ReplayCreateFile (string path, DateTime time)
        {
            string name;
            var parentPath = Utility.GetParentPath (path, out name);
            var parentEntry = Lookup (parentPath) as DirectoryEntry;
            if (parentEntry == null)
                throw new InvalidOperationException ();
            if (parentEntry.Lookup (name) != null)
                throw new InvalidOperationException ();
            var entry = new FileEntry (name, time, null);
            parentEntry.AddChild (entry);
            return entry;
        }

        DirectoryEntry ReplayCreateDirectory (string path, DateTime time)
        {
            string name;
            var parentPath = Utility.GetParentPath (path, out name);
            var parentEntry = Lookup (parentPath) as DirectoryEntry;
            if (parentEntry == null)
                throw new InvalidOperationException ();
            if (parentEntry.Lookup (name) != null)
                throw new InvalidOperationException ();
            var entry = new DirectoryEntry (name, time, null);
            parentEntry.AddChild (entry);
            return entry;
        }

        void ReplayDeleteFile (string path)
        {
            string name;
            var parentPath = Utility.GetParentPath (path, out name);
            var parentEntry = Lookup (parentPath) as DirectoryEntry;
            if (parentEntry == null)
                throw new InvalidOperationException ();
            if (parentEntry.RemoveChild (name) == null)
                throw new InvalidOperationException ();
        }

        void ReplayAppendBlock (string path, DateTime time, SegmentInfo[] segments)
        {
            var entry = Lookup (path) as FileEntry;
            if (entry == null)
                throw new InvalidOperationException ();
            entry.AppendSegments (segments);
        }

        #endregion

        #region File tree operation helpers

        void ValidatePath (string path)
        {
            if (path == null)
                throw new ArgumentNullException ();
            if (path.Length == 0 || path [0] != '/')
                throw new ArgumentException ();
        }

        Entry Lookup (string path)
        {
            if (path.Length == 1 && path [0] == '/')
                return root;
            string[] items = path.Split ('/');
            DirectoryEntry e = root;
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

        #endregion

        #region Metadata classes
        abstract class Entry : FileProperty
        {
            protected Entry (string name, long size, bool isFile, DateTime creationTime)
                : base (name, size, isFile, creationTime)
            {
            }
        }

        sealed class FileEntry : Entry
        {
            static readonly SegmentInfo[] EmptyArray = new SegmentInfo[0];

            public FileEntry (string name, DateTime creationTime, IEnumerable<SegmentInfo> segments)
                : base (name, 0, true, creationTime)
            {
                if (segments == null) {
                    Segments = EmptyArray;
                } else {
                    Segments = new List<SegmentInfo> (segments).ToArray();
                }
                long size = 0;
                foreach (var s in Segments)
                    size += s.Size;
                Size = size;
            }

            public SegmentInfo[] Segments { get; private set; }

            public void AppendSegments (SegmentInfo[] segments)
            {
                lock (this) {
                    var newSegments = new List<SegmentInfo> (Segments);
                    foreach (var s in segments) {
                        if (s.Size == 0)
                            throw new ArgumentException ();
                        newSegments.Add (s);
                        Size += s.Size;
                    }
                    Segments = newSegments.ToArray ();
                }
            }

            public void AppendSegment (SegmentID id, uint size)
            {
                if (size == 0)
                    throw new ArgumentException ();
                lock (this) {
                    var newSegments = new List<SegmentInfo> (Segments);
                    newSegments.Add (new SegmentInfo (id, size));
                    Segments = newSegments.ToArray ();
                    Size += size;
                }
            }
        }

        sealed class DirectoryEntry : Entry
        {
            static readonly Entry[] EmptyArray = new Entry[0];

            public DirectoryEntry (string name, DateTime creationTime, IEnumerable<Entry> children)
                : base (name, 0, false, creationTime)
            {
                if (children == null) {
                    Children = EmptyArray;
                } else {
                    Children = new List<Entry>(children).ToArray();
                }
            }

            public Entry[] Children { get; private set; }

            public void AddChild(Entry entry)
            {
                lock (this) {
                    var newChildren = new List<Entry> (Children);
                    foreach (var c in Children) {
                        if (entry.Name.Equals (c.Name))
                            throw new ArgumentException ();
                    }
                    newChildren.Add (entry);
                    Children = newChildren.ToArray ();
                }
            }

            public Entry RemoveChild (string name)
            {
                lock (this) {
                    int i = IndexOf (name);
                    if (i < 0)
                        return null;
                    var newChildren = new List<Entry> (Children);
                    var removed = newChildren [i];
                    newChildren.RemoveAt (i);
                    Children = newChildren.ToArray ();
                    return removed;
                }
            }

            public Entry Lookup (string name)
            {
                lock (this) {
                    int i = IndexOf (name);
                    if (i < 0)
                        return null;
                    return Children [i];
                }
            }

            int IndexOf (string name)
            {
                for (var i = 0; i < Children.Length; ++i) {
                    if (name.Equals (Children [i].Name)) {
                        return i;
                    }
                }
                return -1;
            }
        }

        struct SegmentInfo
        {
            public readonly SegmentID ID;
            uint _size;

            public SegmentInfo (SegmentID id, uint size)
            {
                ID = id;
                _size = size;
            }

            public uint Size { get { return _size; } }

            public void SetSize (uint size)
            {
                _size = size;
            }
        }

        struct SegmentID
        {
            ulong _0, _1, _2, _3;

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

            public SegmentID (BinaryReader reader)
            {
                _0 = reader.ReadUInt64 ();
                _1 = reader.ReadUInt64 ();
                _2 = reader.ReadUInt64 ();
                _3 = reader.ReadUInt64 ();
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

            public void WriteTo (BinaryWriter writer)
            {
                writer.Write (_0);
                writer.Write (_1);
                writer.Write (_2);
                writer.Write (_3);
            }
        }

        enum OpLogType : byte
        {
            CreateFile = 0,
            CreateDirectory = 1,
            Delete = 2,
            AppendBlock = 3,
        }

        class OpLog
        {
            public OpLogType OpType { get; private set; }
            public string Path { get; private set; }
            public DateTime Time { get; private set; }
            public SegmentInfo[] Segments { get; private set; }

            public OpLog ()
                : this (OpLogType.CreateFile, string.Empty, DateTime.UtcNow, null)
            {
            }

            public OpLog (OpLogType type, string path, DateTime time, SegmentInfo[] segments)
            {
                if (path == null || time.Kind != DateTimeKind.Utc)
                    throw new ArgumentException();

                OpType = type;
                Path = path;
                Time = time;
                Segments = segments;
                switch (type) {
                case OpLogType.CreateFile:
                case OpLogType.CreateDirectory:
                case OpLogType.Delete:
                    break;
                case OpLogType.AppendBlock:
                    if (segments == null || segments.Length == 0)
                        throw new ArgumentException();
                    break;
                default:
                    throw new ArgumentException();
                }
            }

            public void ReadFrom (BinaryReader reader)
            {
                OpType = (OpLogType)reader.ReadByte ();
                Path = Encoding.UTF8.GetString (reader.ReadBytes (reader.ReadInt32 ()));
                Time = new DateTime (reader.ReadInt64 (), DateTimeKind.Utc);
                Segments = new SegmentInfo[reader.ReadByte ()];
                for (int i = 0; i < Segments.Length; ++i) {
                    var segId = new SegmentID (reader);
                    var segSize = reader.ReadUInt32 ();
                    Segments[i] = new SegmentInfo (segId, segSize);
                }
            }

            public void WriteTo (BinaryWriter writer)
            {
                var bytes = Encoding.UTF8.GetBytes (Path);
                writer.Write ((byte)OpType);
                writer.Write (bytes.Length);
                writer.Write (bytes);
                writer.Write (Time.Ticks);
                writer.Write ((byte)(Segments == null ? 0 : Segments.Length));
                if (Segments != null) {
                    for (var i = 0; i < Segments.Length; ++i) {
                        Segments [i].ID.WriteTo (writer);
                        writer.Write (Segments [i].Size);
                    }
                }
            }
        }
        #endregion
    }
}
