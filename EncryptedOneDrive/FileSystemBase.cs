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

namespace EncryptedOneDrive
{
    /// <remarks>全てのインスタンスメソッドはスレッドセーフである必要がある</remarks>
    public abstract class FileSystemBase : IDisposable
    {
        /// <summary>指定されたパスの情報を取得する</summary>
        /// <exception cref="System.ArgumentException">パスが不正</exception>
        /// <exception cref="System.ArgumentNullException">パスがNULL</exception>
        /// <exception cref="System.IO.FileNotFoundException">指定されたパスが存在しない</exception>
        /// <exception cref="System.Security.Authentication.AuthenticationException">トークンの有効期限切れ等による認証失敗</exception>
        /// <exception cref="System.UnauthorizedAccessException">情報を取得する権限がない</exception>
        /// <exception cref="System.TimeoutException">サーバとの通信でタイムアウト</exception>
        /// <exception cref="System.IO.IOException">その他のエラー</exception>
        public abstract FileProperty Stat (string path);

        /// <summary>指定したディレクトリ配下のファイル・ディレクトリ一覧を返却する</summary>
        /// <exception cref="System.ArgumentException">パスが不正</exception>
        /// <exception cref="System.ArgumentNullException">パスがNULL</exception>
        /// <exception cref="System.Security.Authentication.AuthenticationException">トークンの有効期限切れ等による認証失敗</exception>
        /// <exception cref="System.TimeoutException">サーバとの通信でタイムアウト</exception>
        /// <exception cref="System.IO.IOException">その他のエラー</exception>
        public abstract FileProperty[] List (string path);

        /// <summary>指定されたパスが存在するか調べる</summary>
        /// <exception cref="System.ArgumentException">パスが不正</exception>
        /// <exception cref="System.ArgumentNullException">パスがNULL</exception>
        /// <exception cref="System.Security.Authentication.AuthenticationException">トークンの有効期限切れ等による認証失敗</exception>
        /// <exception cref="System.UnauthorizedAccessException">情報を取得する権限がない</exception>
        /// <exception cref="System.TimeoutException">サーバとの通信でタイムアウト</exception>
        /// <exception cref="System.IO.IOException">その他のエラー</exception>
        public virtual bool Exists (string path)
        {
            try {
                Stat (path);
                return true;
            } catch (FileNotFoundException) {
                return false;
            }
        }

        /// <summary>指定されたパスを削除する</summary>
        /// <exception cref="System.ArgumentException">パスが不正</exception>
        /// <exception cref="System.ArgumentNullException">パスがNULL</exception>
        /// <exception cref="System.IO.FileNotFoundException">指定されたパスが存在しない</exception>
        /// <exception cref="System.Security.Authentication.AuthenticationException">トークンの有効期限切れ等による認証失敗</exception>
        /// <exception cref="System.UnauthorizedAccessException">オブジェクトを削除する権限がない</exception>
        /// <exception cref="System.TimeoutException">サーバとの通信でタイムアウト</exception>
        /// <exception cref="System.IO.IOException">その他のエラー</exception>
        public abstract void Delete (string path);

        public virtual void DeleteFile (string path)
        {
            Delete (path);
        }

        public virtual void DeleteDirectory (string path)
        {
            Delete (path);
        }

        /// <summary>ディレクトリを作成する (中間ディレクトリがない場合は中間ディレクトリも作成する)</summary>
        /// <exception cref="System.ArgumentException">パスが不正</exception>
        /// <exception cref="System.ArgumentNullException">パスがNULL</exception>
        /// <exception cref="System.Security.Authentication.AuthenticationException">トークンの有効期限切れ等による認証失敗</exception>
        /// <exception cref="System.UnauthorizedAccessException">ディレクトリを作成する権限がない</exception>
        /// <exception cref="System.TimeoutException">サーバとの通信でタイムアウト</exception>
        /// <exception cref="System.IO.IOException">その他のエラー (中間ディレクトリ等がファイルである場合も含む)</exception>
        public abstract FileProperty CreateDirectory (string path);

        /// <summary>指定されたパスのコンテンツを取得する</summary>
        /// <exception cref="System.ArgumentException">パスが不正</exception>
        /// <exception cref="System.ArgumentNullException">パスがNULL</exception>
        /// <exception cref="System.IO.FileNotFoundException">指定されたパスが存在しない</exception>
        /// <exception cref="System.Security.Authentication.AuthenticationException">トークンの有効期限切れ等による認証失敗</exception>
        /// <exception cref="System.UnauthorizedAccessException">オブジェクトを取得する権限がない</exception>
        /// <exception cref="System.TimeoutException">サーバとの通信でタイムアウト</exception>
        /// <exception cref="System.IO.IOException">その他のエラー</exception>
        public abstract Stream ReadOpen (string path, out FileProperty stat);

        /// <summary>指定されたパスのコンテンツを取得する</summary>
        /// <exception cref="System.ArgumentException">パスが不正</exception>
        /// <exception cref="System.ArgumentNullException">パスがNULL</exception>
        /// <exception cref="System.IO.FileNotFoundException">指定されたパスが存在しない</exception>
        /// <exception cref="System.Security.Authentication.AuthenticationException">トークンの有効期限切れ等による認証失敗</exception>
        /// <exception cref="System.UnauthorizedAccessException">取得する権限がない</exception>
        /// <exception cref="System.TimeoutException">サーバとの通信でタイムアウト</exception>
        /// <exception cref="System.IO.IOException">その他のエラー</exception>
        public virtual Stream ReadOpen (string path)
        {
            FileProperty stat;
            return ReadOpen (path, out stat);
        }

        /// <summary>指定されたパスのコンテンツを取得する</summary>
        /// <exception cref="System.ArgumentException">パスが不正</exception>
        /// <exception cref="System.ArgumentNullException">パスがNULL</exception>
        /// <exception cref="System.IO.FileNotFoundException">指定されたパスが存在しない</exception>
        /// <exception cref="System.Security.Authentication.AuthenticationException">トークンの有効期限切れ等による認証失敗</exception>
        /// <exception cref="System.UnauthorizedAccessException">取得する権限がない</exception>
        /// <exception cref="System.TimeoutException">サーバとの通信でタイムアウト</exception>
        /// <exception cref="System.IO.IOException">その他のエラー</exception>
        public virtual byte[] ReadAllBytes (string path)
        {
            FileProperty stat;
            using (Stream strm = ReadOpen (path, out stat)) {
                byte[] raw = new byte[stat.Size];
                strm.ReadFull (raw, 0, raw.Length);
                return raw;
            }
        }

        /// <summary>指定されたパスに格納する</summary>
        /// <exception cref="System.ArgumentException">パスが不正</exception>
        /// <exception cref="System.ArgumentNullException">パスがNULL</exception>
        /// <exception cref="System.Security.Authentication.AuthenticationException">トークンの有効期限切れ等による認証失敗</exception>
        /// <exception cref="System.UnauthorizedAccessException">格納する権限がない</exception>
        /// <exception cref="System.TimeoutException">サーバとの通信でタイムアウト</exception>
        /// <exception cref="System.IO.IOException">その他のエラー (オブジェクトが大きすぎる場合を含む)</exception>
        public abstract void WriteAll (string path, Stream strm);

        /// <summary>指定されたパスに格納する</summary>
        /// <exception cref="System.ArgumentException">パスが不正</exception>
        /// <exception cref="System.ArgumentNullException">パスがNULL</exception>
        /// <exception cref="System.Security.Authentication.AuthenticationException">トークンの有効期限切れ等による認証失敗</exception>
        /// <exception cref="System.UnauthorizedAccessException">格納する権限がない</exception>
        /// <exception cref="System.TimeoutException">サーバとの通信でタイムアウト</exception>
        /// <exception cref="System.IO.IOException">その他のエラー (オブジェクトが大きすぎる場合を含む)</exception>
        public virtual void WriteAllBytes (string path, byte[] data)
        {
            WriteAllBytes (path, data, 0, data.Length);
        }

        /// <summary>指定されたパスに格納する</summary>
        /// <exception cref="System.ArgumentException">パスが不正</exception>
        /// <exception cref="System.ArgumentNullException">パスがNULL</exception>
        /// <exception cref="System.Security.Authentication.AuthenticationException">トークンの有効期限切れ等による認証失敗</exception>
        /// <exception cref="System.UnauthorizedAccessException">格納する権限がない</exception>
        /// <exception cref="System.TimeoutException">サーバとの通信でタイムアウト</exception>
        /// <exception cref="System.IO.IOException">その他のエラー (オブジェクトが大きすぎる場合を含む)</exception>
        public virtual void WriteAllBytes (string path, byte[] data, int index, int count)
        {
            using (var strm = new MemoryStream (data, index, count)) {
                WriteAll (path, strm);
            }
        }

        public virtual Stream WriteOpen (string path)
        {
            return new WriteStream (path, this);
        }

        /// <summary>ファイルシステムの使用状況を取得する</summary>
        /// <exception cref="System.Security.Authentication.AuthenticationException">トークンの有効期限切れ等による認証失敗</exception>
        /// <exception cref="System.UnauthorizedAccessException">情報を取得する権限がない</exception>
        /// <exception cref="System.TimeoutException">サーバとの通信でタイムアウト</exception>
        /// <exception cref="System.IO.IOException">その他のエラー</exception>
        public abstract void GetStorageUsage (
            out long totalSize, out long availableSize);

        public abstract void Dispose ();

        class WriteStream : Stream
        {
            string _path;
            FileSystemBase _fs;
            MemoryStream _strm = new MemoryStream ();

            public WriteStream (string path, FileSystemBase fs)
            {
                _path = path;
                _fs = fs;
            }

            public override void Close ()
            {
                base.Close ();
                if (_strm == null)
                    return;
                _strm.Seek (0, SeekOrigin.Begin);
                _fs.WriteAll (_path, _strm);
                _strm.Close ();
            }

            public override void Write (byte[] buffer, int offset, int count)
            {
                _strm.Write (buffer, offset, count);
            }
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { return _strm.Length; } }
            public override long Position {
                get {
                    return Length;
                }
                set {
                    throw new NotSupportedException ();
                }
            }
            public override void Flush ()
            {
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
        }
    }
}
