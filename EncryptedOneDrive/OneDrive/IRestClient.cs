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

using System.IO;

namespace EncryptedOneDrive.OneDrive
{
    public interface IRestClient
    {
        string Upload (string folderId, string name, Stream strm);

        Stream Download (string fileId);

        ObjectProperty GetProperty (string objectId);

        ObjectProperty[] GetChildren (string folderId);

        ObjectProperty CreateDirectory (string folderId, string name);

        void Delete (string objectId);

        void GetQuota (out long quota, out long available);
    }
}
