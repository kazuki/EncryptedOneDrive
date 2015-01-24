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
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace EncryptedOneDrive
{
    /// <summary>
    /// OneDrive REST API Client
    /// </summary>
    public class OldOneDriveClient
    {
        const string LiveBaseUri = "https://apis.live.net/v5.0/";
        const string PseudoRootFolderId = "me/skydrive";
        const string MIME_JSON = "application/json";
        static ThreadLocal<DataContractJsonSerializer> FilesSerDe = new ThreadLocal<DataContractJsonSerializer>(() => new DataContractJsonSerializer (typeof(JsonFilesResponse)));
        static ThreadLocal<DataContractJsonSerializer> FileEntrySerDe = new ThreadLocal<DataContractJsonSerializer>(() => new DataContractJsonSerializer (typeof(JsonFilesResponseEntry)));
        Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry> ();
        ReaderWriterLockSlim _cacheLock = new ReaderWriterLockSlim ();

        public OldOneDriveClient (string token)
        {
            if (token.IndexOf ('+') >= 0 || token.IndexOf ('=') >= 0) {
                AccessToken = token;
                AccessTokenEscaped = Uri.EscapeDataString (token);
            } else {
                AccessToken = Uri.UnescapeDataString (token);
                AccessTokenEscaped = token;
            }
            CacheLifetime = TimeSpan.FromHours (1);
            GetEntry ("/");
        }

        public TimeSpan CacheLifetime { get; set; }
        private string AccessToken { get; set; }
        private string AccessTokenEscaped { get; set; }

        public OneDriveEntry[] GetFiles (string path)
        {
            path = NormalizePath (path);
            string id = Resolve (path);
            return GetFilesInternal (path, id);
        }

        public bool CreateDirectory (string path)
        {
            path = NormalizePath (path);
            if (path == "/" || GetCacheEntry(path) != null)
                return false;
            int pos = path.LastIndexOf ("/", StringComparison.InvariantCulture);
            string name = path.Substring (pos + 1);
            if (name.Length == 0)
                throw new ArgumentException ();
            string parentPath = path.Substring (0, pos);
            string parentId = Resolve (parentPath);
            if (parentId == null) {
                CreateDirectory (parentPath);
                parentId = Resolve (parentPath);
                if (parentId == null)
                    throw new IOException ();
            }
            return CreateDirectory (parentId, name, path);
        }

        public bool Delete (string path)
        {
            path = NormalizePath (path);
            if (path == "/")
                return false;
            string id = Resolve (path);
            if (id == null)
                return false;
            var ret = DeleteObject (path, id);

            List<string> removeKeys = new List<string> ();
            _cacheLock.EnterReadLock ();
            foreach (string key in _cache.Keys) {
                if (key.StartsWith (path, StringComparison.InvariantCulture))
                    removeKeys.Add (key);
            }
            _cacheLock.ExitReadLock ();
            if (removeKeys.Count > 0) {
                _cacheLock.EnterWriteLock ();
                foreach (var key in removeKeys)
                    _cache.Remove (key);
                _cacheLock.ExitWriteLock ();
            }
            return ret;
        }

        public OneDriveEntry GetEntry (string path, bool latest = false)
        {
            path = NormalizePath (path);
            bool fetchNow = (GetCacheEntry (path) == null);
            string id = Resolve (path);
            if (id == null)
                return null;
            var x = GetCacheEntry (path);
            if (x == null)
                return null;
            if (latest && !fetchNow) {
                string uri = LiveBaseUri + id + "?access_token=" + AccessTokenEscaped;
                Console.WriteLine ("[OneDriveClient] Get Property: {0} {1}", path, uri);
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create (uri);
                JsonFilesResponseEntry data;
                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse ()) {
                    if (res.StatusCode != HttpStatusCode.OK)
                        throw new HttpException (res.StatusCode);
                    data = (JsonFilesResponseEntry)FileEntrySerDe.Value.ReadObject (res.GetResponseStream ());
                }
                _cacheLock.EnterWriteLock ();
                if (_cache.TryGetValue (path, out x)) {
                    x.Entry = new OneDriveEntry (data.id, data.name, data.IsFile, data.size);
                }
                _cacheLock.ExitWriteLock ();
            }
            return x.Entry;
        }

        public bool Exists (string path)
        {
            path = NormalizePath (path);
            return (Resolve (path) != null);
        }

        public bool IsFile (string path)
        {
            path = NormalizePath (path);
            string id = Resolve (path);
            if (id == null)
                return false;
            var x = GetCacheEntry (path);
            if (x == null)
                return false;
            return x.Entry.IsFile;
        }

        public bool IsDirectory (string path)
        {
            path = NormalizePath (path);
            string id = Resolve (path);
            if (id == null)
                return false;
            var x = GetCacheEntry (path);
            if (x == null)
                return false;
            return x.Entry.IsFolder;
        }

        public Stream Download (string path)
        {
            long contentLength;
            return Download (path, out contentLength);
        }

        public Stream Download (string path, out long contentLength)
        {
            contentLength = -1;
            path = NormalizePath (path);
            string id = Resolve (path);
            if (id == null)
                throw new FileNotFoundException ();
            var x = GetCacheEntry (path);
            if (x == null)
                throw new FileNotFoundException ();
            if (x.Entry.IsFolder)
                throw new IOException ();
                
            string uri = LiveBaseUri + id + "/content?access_token=" + AccessTokenEscaped;
            Console.WriteLine ("[OneDriveClient] Download: {0} {1}", path, uri);
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create (uri);
            HttpWebResponse res = (HttpWebResponse)req.GetResponse ();
            if (res.StatusCode != HttpStatusCode.OK)
                throw new HttpException (res.StatusCode);
            contentLength = res.ContentLength;
            return res.GetResponseStream ();
        }

        public byte[] DownloadBytes (string path)
        {
            long len;
            using (Stream strm = Download (path, out len)) {
                byte[] raw = new byte[len];
                int read = 0;
                while (read < raw.Length) {
                    int ret = strm.Read (raw, read, raw.Length - read);
                    if (ret <= 0)
                        throw new IOException ();
                    read += ret;
                }
                return raw;
            }
        }

        public void Upload (string path, byte[] raw)
        {
            Upload (path, raw, 0, raw.Length);
        }

        public void Upload (string path, byte[] raw, int offset, int count)
        {
            Upload (path, new MemoryStream (raw, offset, count));
        }

        /// <returns>>true: created, false: overwrite</returns>
        public bool Upload (string path, Stream strm)
        {
            path = NormalizePath (path);
            int pos = path.LastIndexOf ("/", StringComparison.InvariantCulture);
            string name = path.Substring (pos + 1);
            if (name.Length == 0)
                throw new ArgumentException ();
            string parentPath = path.Substring (0, pos);
            string parentId = Resolve (parentPath);

            string uri = LiveBaseUri + parentId + "/files/" + name + "?access_token=" + AccessTokenEscaped;
            Console.WriteLine ("[OneDriveClient] Upload: {0} {1}", path, uri);
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create (uri);
            req.Method = "PUT";
            req.ContentLength = strm.Length;
            using (Stream dst = req.GetRequestStream ()) {
                strm.CopyTo (dst);
            }
            using (HttpWebResponse res = (HttpWebResponse)req.GetResponse ()) {
                if (res.StatusCode == HttpStatusCode.Created) {
                    string id = res.Headers ["Location"].Substring (LiveBaseUri.Length).Trim (new char[]{ '/' });
                    _cacheLock.EnterWriteLock ();
                    _cache [path] = new CacheEntry {
                        Entry = new OneDriveEntry (id, name, true, req.ContentLength),
                        Expiration = DateTime.UtcNow + CacheLifetime
                    };
                    _cacheLock.ExitWriteLock ();
                    return true;
                }
                return false;
            }
        }

        string NormalizePath (string path)
        {
            while (path.IndexOf ("//", StringComparison.InvariantCulture) >= 0)
                path = path.Replace ("//", "/");
            if (!path.StartsWith ("/", StringComparison.InvariantCulture))
                path = "/" + path;
            if (path.EndsWith ("/", StringComparison.InvariantCulture))
                path = path.Substring (0, path.Length - 1);
            return path;
        }

        /// <summary>
        /// Resolve folder/file id from path
        /// </summary>
        string Resolve (string path)
        {
            if (!path.StartsWith ("/", StringComparison.InvariantCulture) && path.Length > 0)
                throw new ArgumentException ();
            if (path.Length <= 1) {
                return PseudoRootFolderId;
            }

            int pos = path.LastIndexOf ('/');
            string name = path.Substring (pos + 1);
            string parent = path.Substring (0, pos);
            if (parent.Length == 0)
                parent = "/";
            string parentId = Resolve (parent);
            if (name.Length == 0)
                return parentId;

            var e = GetCacheEntry (path);
            if (e == null) {
                updateCache (parent, parentId);
                e = GetCacheEntry (path);
                if (e == null)
                    return null;
            }
            return e.Entry.ID;
        }

        CacheEntry GetCacheEntry (string path)
        {
            CacheEntry entry;
            _cacheLock.EnterReadLock ();
            if (!_cache.TryGetValue (path, out entry))
                entry = null;
            _cacheLock.ExitReadLock ();
            return entry;
        }

        /// <summary>
        /// Updates the cache (if id is null, update "/")
        /// </summary>
        JsonFilesResponseEntry[] updateCache (string path, string id)
        {
            if (!path.EndsWith ("/", StringComparison.InvariantCulture))
                path += "/";

            string uri = LiveBaseUri + id + "/files?access_token=" + AccessTokenEscaped;
            Console.WriteLine ("[OneDriveClient] List: {0} {1}", path, uri);
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create (uri);
            JsonFilesResponseEntry[] data;
            using (HttpWebResponse res = (HttpWebResponse)req.GetResponse ()) {
                if (res.StatusCode != HttpStatusCode.OK)
                    throw new HttpException (res.StatusCode);
                data = ((JsonFilesResponse)FilesSerDe.Value.ReadObject (res.GetResponseStream ())).data;
            }
            var expiration = DateTime.UtcNow + CacheLifetime;
            _cacheLock.EnterWriteLock ();
            foreach (var x in data) {
                string x_path = path + x.name;
                CacheEntry entry;
                if (!_cache.TryGetValue (x_path, out entry)) {
                    entry = new CacheEntry ();
                    entry.Entry = null;
                    _cache.Add (x_path, entry);
                }
                entry.Expiration = expiration;
                if (entry.Entry == null || entry.Entry.ID != x.id) {
                    entry.Entry = new OneDriveEntry (x.id, x.name, x.IsFile, x.size);
                }
            }
            _cacheLock.ExitWriteLock ();
            return data;
        }

        OneDriveEntry[] GetFilesInternal (string path, string folderId)
        {
            if (!folderId.StartsWith ("folder.", StringComparison.InvariantCulture) && folderId != PseudoRootFolderId)
                throw new ArgumentException ();
            var data = updateCache (path, folderId);
            OneDriveEntry[] entries = new OneDriveEntry[data.Length];
            for (int i = 0; i < data.Length; ++i) {
                var x = data [i];
                entries [i] = new OneDriveEntry (x.id, x.name, x.IsFile, x.size);
            }
            return entries;
        }

        bool CreateDirectory (string parentFolderId, string name, string fullPath)
        {
            string uri = LiveBaseUri + parentFolderId;
            Console.WriteLine ("[OneDriveClient] mkdir: {0} {1}", fullPath, uri);
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create (uri);
            req.Method = "POST";
            req.ContentType = MIME_JSON;
            req.Headers.Add ("Authorization", "Bearer " + AccessToken);
            using (var strm = req.GetRequestStream ()) {
                byte[] raw = Encoding.UTF8.GetBytes ("{\"name\": \"" + name + "\"}");
                strm.Write (raw, 0, raw.Length);
            }
            try {
                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse ()) {
                    if (res.StatusCode == HttpStatusCode.Created) {
                        string id = res.Headers["Location"].Substring(LiveBaseUri.Length).Trim(new char[]{'/'});
                        _cacheLock.EnterWriteLock();
                        _cache[fullPath] = new CacheEntry {
                            Entry = new OneDriveEntry (id, name, false, 0),
                            Expiration = DateTime.UtcNow + CacheLifetime
                        };
                        _cacheLock.ExitWriteLock();
                        return true;
                    }
                }
            } catch {}
            return false; // 既にある場合は400が帰ってくるので...
        }

        bool DeleteObject (string path, string id)
        {
            string uri = LiveBaseUri + id + "?access_token=" + AccessTokenEscaped;
            Console.WriteLine ("[OneDriveClient] delete {0} {1}", path, uri);
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create (uri);
            req.Method = "DELETE";
            try {
                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse ()) {
                    if (res.StatusCode == HttpStatusCode.NoContent)
                        return true;
                }
            } catch {}
            return false;
        }

        #region Internal Classes
        class CacheEntry
        {
            public OneDriveEntry Entry { get; set; }
            public DateTime Expiration { get; set; }
        }

        [DataContract]
        class JsonFilesResponse
        {
            [DataMember]
            public JsonFilesResponseEntry[] data;
        }

        [DataContract]
        class JsonFilesResponseEntry
        {
            [DataMember]
            public string id;

            [DataMember]
            public string name;

            [DataMember]
            public string type;

            [DataMember]
            public long size;

            public bool IsFile {
                get { return type == "file"; }
            }
        }

        class HttpException : Exception
        {
            public HttpException(HttpStatusCode code)
            {
                this.Code = code;
            }

            public HttpStatusCode Code { get; private set; }
        }
        #endregion
    }

    public class OneDriveEntry
    {
        public OneDriveEntry(string id, string name, bool isFile, long size)
        {
            this.ID = id;
            this.Name = name;
            this.IsFile = isFile;
            this.Size = size;
        }

        public string ID { get; private set; }
        public string Name { get; private set; }
        public bool IsFile { get; private set; }
        public bool IsFolder { get { return !IsFile; } }
        public long Size { get; private set; }
    }
}
