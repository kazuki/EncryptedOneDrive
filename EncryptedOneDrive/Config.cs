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
using System.Xml;
using System.Reflection;
using System.Text;

namespace EncryptedOneDrive
{
    public class Config
    {
        readonly Dictionary<string, string> kv = new Dictionary<string, string> ();

        public Config ()
        {
            ApplicationDataDirectory = Path.Combine (
                Environment.GetFolderPath (Environment.SpecialFolder.ApplicationData),
                Assembly.GetExecutingAssembly ().GetName().Name);
            ConfigFilePath = Path.Combine (ApplicationDataDirectory, "config.xml");

            if (!Directory.Exists (ApplicationDataDirectory))
                Directory.CreateDirectory (ApplicationDataDirectory);

            Load ();
        }

        public string ApplicationDataDirectory { get; private set; }
        public string ConfigFilePath { get; private set; }

        void Load ()
        {
            if (!File.Exists (ConfigFilePath))
                return;

            using (var reader = new XmlTextReader (ConfigFilePath)) {
                while (reader.Read () && reader.NodeType != XmlNodeType.Element);
                if (reader.NodeType != XmlNodeType.Element || reader.Name != "config")
                    return;
                while (reader.Read ()) {
                    if (reader.NodeType == XmlNodeType.Element) {
                        Load (reader.Name, reader);
                    }
                }
            }
        }

        void Load (string path, XmlReader reader)
        {
            if (reader.IsEmptyElement) {
                if (reader.MoveToAttribute ("value"))
                    kv[path] = reader.Value.Trim ();
                return;
            }

            while (reader.Read ()) {
                switch (reader.NodeType) {
                    case XmlNodeType.EndElement:
                        return;
                    case XmlNodeType.Element:
                        Load (path + "." + reader.Name, reader);
                        break;
                    case XmlNodeType.Text:
                        kv[path] = reader.Value.Trim ();
                        break;
                }
            }
        }

        public void Save ()
        {
            if (!Directory.Exists (ApplicationDataDirectory))
                Directory.CreateDirectory (ApplicationDataDirectory);
            using (var writer = new XmlTextWriter (ConfigFilePath, Encoding.UTF8)) {
                writer.Indentation = 2;
                writer.IndentChar = ' ';
                writer.Formatting = Formatting.Indented;
                writer.WriteStartDocument ();
                writer.WriteStartElement ("config");
                Save ("", writer);
                writer.WriteEndElement ();
                writer.WriteEndDocument ();
            }
        }

        void Save (string prefix, XmlWriter writer)
        {
            var subkeys = new HashSet<string> ();
            int pos;
            string value = null;

            foreach (var pair in kv) {
                if (!pair.Key.StartsWith (prefix))
                    continue;
                if (prefix.Length == pair.Key.Length) {
                    value = pair.Value;
                    break;
                }

                pos = pair.Key.IndexOf ('.', prefix.Length + 1);
                if (pos < 0)
                    pos = pair.Key.Length;
                subkeys.Add (pair.Key.Substring (0, pos));
            }

            if (prefix.Length > 0) {
                pos = prefix.LastIndexOf ('.');
                writer.WriteStartElement (prefix.Substring (pos + 1));
                if (value != null) {
                    writer.WriteAttributeString ("value", value);
                }
            }
            if (value == null) {
                foreach (var key in subkeys)
                    Save (key, writer);
            }
            if (prefix.Length > 0)
                writer.WriteEndElement ();
        }

        public string Get (string name, string defaultValue)
        {
            string value;
            if (kv.TryGetValue (name, out value))
                return value;
            return defaultValue;
        }

        public long Get (string name, long defaultValue)
        {
            string str;
            long value;
            if (kv.TryGetValue (name, out str) && long.TryParse (str, out value))
                return value;
            return defaultValue;
        }

        public double Get (string name, double defaultValue)
        {
            string str;
            double value;
            if (kv.TryGetValue (name, out str) && double.TryParse (str, out value))
                return value;
            return defaultValue;
        }

        public bool Get (string name, bool defaultValue)
        {
            string str;
            bool value;
            if (kv.TryGetValue (name, out str) && bool.TryParse (str, out value))
                return value;
            return defaultValue;
        }

        public void Delete (string name)
        {
            kv.Remove (name);
        }

        public void Set (string name, string value)
        {
            if (value == null) {
                Delete (name);
                return;
            }
            kv[name] = value;
        }

        public void Set (string name, long value)
        {
            kv[name] = value.ToString();
        }

        public void Set (string name, double value)
        {
            kv[name] = value.ToString();
        }

        public void Set (string name, bool value)
        {
            kv[name] = value.ToString();
        }
    }
}
