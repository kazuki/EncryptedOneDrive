// Copyright (C) 2015  Kazuki Oikawa
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
using System.Security.Cryptography;
using NUnit.Framework;
using EncryptedOneDrive.Security;

namespace EncryptedOneDrive.Tests.Security
{
    [TestFixture ()]
    public class BlockCipherCTRHMACTest
    {
        readonly BlockCipherCTRHMAC<AesManaged, HMACSHA1> instance =
            new BlockCipherCTRHMAC<AesManaged, HMACSHA1>();
        readonly RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();

        [Test]
        public void RoundtripTest()
        {
            byte[] key = new byte[instance.KeyByteSize];
            byte[] iv = new byte[instance.IVByteSize];
            byte[] tag;
            byte[] plain = new byte[1024 * 1024];
            byte[] encrypted = new byte[plain.Length];
            byte[] decrypted = new byte[plain.Length];
            lock (rng) {
                rng.GetBytes (plain);
            }
            using (var ct = instance.CreateEncryptor (key, iv, null)) {
                ct.TransformBlock (
                    (byte[])plain.Clone (),
                    0,
                    plain.Length,
                    encrypted,
                    0);
                tag = ct.TransformFinal ();
            }
            using (var ct = instance.CreateDecryptor (key, iv, null, tag)) {
                ct.TransformBlock (
                    encrypted,
                    0,
                    encrypted.Length,
                    decrypted,
                    0);
                ct.TransformFinal ();
            }
            Assert.AreEqual (plain, decrypted);
        }

        [Test]
        public void MultipleShortBlockTest()
        {
            byte[] key = new byte[instance.KeyByteSize];
            byte[] iv = new byte[instance.IVByteSize];
            byte[] tag0, tag1;
            byte[] plain = new byte[1024 * 1024 * 8];
            byte[] encrypted0 = new byte[plain.Length];
            byte[] encrypted1 = new byte[plain.Length];
            lock (rng) {
                rng.GetBytes (plain);
            }
            using (var ct = instance.CreateEncryptor (key, iv, null)) {
                ct.TransformBlock (
                    (byte[])plain.Clone (),
                    0,
                    plain.Length,
                    encrypted0,
                    0);
                tag0 = ct.TransformFinal ();
            }
            using (var ct = instance.CreateEncryptor (key, iv, null)) {
                int off = 0;
                Random rnd = new Random ();
                while (off < plain.Length) {
                    int size = Math.Min (
                                   plain.Length - off,
                                   rnd.Next (
                                       64,
                                       1024 * 16));
                    ct.TransformBlock (
                        plain,
                        off,
                        size,
                        encrypted1,
                        off);
                    off += size;
                }
                tag1 = ct.TransformFinal ();
            }
            Assert.AreEqual (tag0, tag1);
            Assert.AreEqual (encrypted0, encrypted1);
        }
    }
}
