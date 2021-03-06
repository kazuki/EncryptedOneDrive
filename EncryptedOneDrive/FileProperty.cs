﻿// Copyright (C) 2014  Kazuki Oikawa
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

namespace EncryptedOneDrive
{
    public class FileProperty
    {
        public string Name { get; protected set; }
        public long Size { get; protected set; }
        public DateTime CreationTime { get; protected set; }

        public bool IsFile { get; protected set; }
        public bool IsDirectory { get { return !IsFile; } }

        public FileProperty (string name, long size,
            bool isFile, DateTime creationTime)
        {
            Name = name;
            Size = size;
            IsFile = isFile;
            CreationTime = creationTime;
        }
    }
}
