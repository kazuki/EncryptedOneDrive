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
using EncryptedOneDrive;
using LiveConnectClient = EncryptedOneDrive.Security.LiveConnectClient;

namespace EncryptedOneDrive.OneDrive
{
    public class FileSystem : FileSystemBase
    {
        bool disposed = false;
        readonly Config config;
        readonly IRestClient rest;
        readonly IKeyValueStore cache;

        public FileSystem (Config config, LiveConnectClient auth)
            : this (config, new RestClient(auth), new LevelDB())
        {
            this.cache.Opne (Path.Combine (this.config.ApplicationDataDirectory, "onedrive.cache"));
        }

        internal FileSystem (Config config, IRestClient client, IKeyValueStore cache)
        {
            this.config = config;
            this.rest = client;
            this.cache = cache;
        }

        public override FileProperty Stat (string path)
        {
            return ToObjectInfo (GetObjectProperty (path));
        }

        public override FileProperty[] List (string path)
        {
            var prop = GetObjectProperty (path);
            if (!prop.IsFolder)
                throw new IOException ();
            var list = GetObjectList (prop.ID);
            var ret = new FileProperty[list.Length];
            for (int i = 0; i < list.Length; ++i)
                ret [i] = ToObjectInfo (list [i]);
            return ret;
        }

        public override void Delete (string path)
        {
            var prop = GetObjectProperty (path);
            rest.Delete (prop.ID);
        }

        public override FileProperty CreateDirectory (string path)
        {
            ObjectProperty prop;
            try {
                prop = GetObjectProperty (path);
            } catch {
                prop = CreateDirectoryInternal (path);
            }
            return ToObjectInfo (prop);
        }

        ObjectProperty CreateDirectoryInternal (string path)
        {
            string name;
            var parentPath = Utility.GetParentPath (path, out name);
            ObjectProperty parentProp;
            try {
                parentProp = GetObjectProperty (parentPath);
            } catch (FileNotFoundException) {
                parentProp = CreateDirectoryInternal (parentPath);
            }
            Console.WriteLine ("CreateDirectory {0}", path);
            return rest.CreateDirectory (parentProp.ID, name);
        }

        public override Stream ReadOpen (string path, out FileProperty stat)
        {
            stat = null;
            var prop = GetObjectProperty (path);
            if (!prop.IsFile)
                throw new IOException ();
            stat = ToObjectInfo (prop);
            return rest.Download (prop.ID);
        }

        public override void WriteAll (string path, Stream strm)
        {
            string name;
            var parentPath = Utility.GetParentPath (path, out name);
            var parentProp = GetObjectProperty (parentPath);
            if (!parentProp.IsFolder)
                throw new IOException ();
            rest.Upload (parentProp.ID, name, strm);
        }

        public override void GetStorageUsage (out long totalSize, out long availableSize)
        {
            rest.GetQuota (out totalSize, out availableSize);
        }

        public override void Dispose ()
        {
            if (disposed)
                return;

            disposed = true;
            if (cache != null)
                cache.Dispose ();
        }

        static FileProperty ToObjectInfo (ObjectProperty prop)
        {
            DateTime createdTime = DateTime.MinValue;
            if (prop.CreatedTime != null)
                createdTime = DateTime.ParseExact (prop.CreatedTime, @"yyyy-MM-dd\THH:mm:ssK", null);
            return new FileProperty (
                prop.Name, prop.Size, prop.IsFile, createdTime
            );
        }

        ObjectProperty GetObjectProperty (string path)
        {
            if (path == null)
                throw new ArgumentNullException ();
            while (path.Length > 1 && path [path.Length - 1] == '/')
                path = path.Substring (0, path.Length - 1);
            if (path.Length == 0 || path[0] != '/')
                throw new ArgumentException ();
            if (path.Length == 1) {
                return rest.GetProperty (RestClient.RootID);
            }

            ObjectProperty prop = null;
            string parentId = RestClient.RootID;
            int pos = 0;
            do {
                int prev_pos = pos + 1;
                pos = path.IndexOf ('/', prev_pos);
                if (pos < 0)
                    pos = path.Length;

                var children = rest.GetChildren (parentId);
                var name = path.Substring (prev_pos, pos - prev_pos);
                ObjectProperty nextParentProp = null;
                foreach (var c in children) {
                    if (c.Name != name)
                        continue;
                    nextParentProp = c;
                    break;
                }
                if (nextParentProp == null)
                    throw new FileNotFoundException();
                if (nextParentProp.IsFile && pos < path.Length)
                    throw new FileNotFoundException();
                parentId = nextParentProp.ID;
                prop = nextParentProp;
            } while (pos < path.Length);

            return prop;
        }

        ObjectProperty[] GetObjectList (string folderId)
        {
            return rest.GetChildren (folderId);
        }
    }
}
