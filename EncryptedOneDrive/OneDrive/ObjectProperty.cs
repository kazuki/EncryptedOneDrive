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

using System.Runtime.Serialization;

namespace EncryptedOneDrive.OneDrive
{
    [DataContract]
    public class ObjectProperty
    {
        [DataMember(Name = "id")]
        public string ID { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "description")]
        public string Description { get; set; }

        [DataMember(Name = "parent_id")]
        public string ParentID { get; set; }

        [DataMember(Name = "size")]
        public long Size { get; set; }

        [DataMember(Name = "type")]
        public string ObjectType { get; set; }

        [DataMember(Name = "created_time")]
        public string CreatedTime { get; set; }

        [DataMember(Name = "updated_time")]
        public string UpdatedTime { get; set; }

        public bool IsFile { get { return ObjectType == "file"; } }
        public bool IsFolder { get { return ObjectType == "folder"; } }
    }
}
