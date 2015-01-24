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
using System.Diagnostics;
using EncryptedOneDrive.Security;
using EncryptedOneDrive;
using KVS = EncryptedOneDrive.LevelDB;

namespace EncryptedOneDrive
{
    public class AppLoader
    {
        static void Main (string[] args)
        {
            Config cfg = new Config ();
            var liveClient = Login (cfg);
            CryptoManager cryptoMgr;
            if (cfg.Get ("expanded-password", null) == null) {
                var password = cfg.Get ("password", null);
                if (password == null) {
                    Console.WriteLine ("passwordを指定してね");
                    return;
                }
                var salt = cfg.Get ("password-salt", "encrypted-userspace-overlay-filesystem-over-cloud-storage");
                cryptoMgr = new CryptoManager (password, salt);
                cfg.Set ("expanded-password", Convert.ToBase64String (cryptoMgr.Key));
                cfg.Delete ("password");
                cfg.Save ();
            } else {
                cryptoMgr = new CryptoManager (Convert.FromBase64String (cfg.Get ("expanded-password", null)));
            }
            using (var baseFS = new OneDrive.FileSystem (cfg, liveClient))
            using (FileSystem fs = new FileSystem (cfg, baseFS, cryptoMgr)) {
                if (Environment.OSVersion.Platform == PlatformID.Unix) {
                    // Mono-FUSE
                    using (Fuse fuse = new Fuse (fs)) {
                        args = fuse.ParseFuseArguments (args);
                        fuse.MountPoint = "/mnt/fuse";
                        fuse.Start();
                    }
                } else {
                    // Dokan.NET (TODO)
                }
            }
        }

        internal static LiveConnectClient Login(Config cfg)
        {
        Retry:
            var liveScopes = new string[] {
                "wl.signin", "wl.basic", "wl.contacts_skydrive", "wl.skydrive",
                "wl.skydrive_update", "wl.offline_access"
            };
            var liveClientId = cfg.Get ("onedrive.auth.client-id", "0000000040137674");
            var live = new LiveConnectClient (liveClientId, liveScopes);
            live.Authorized += (object sender, EventArgs e) => cfg.Delete ("onedrive.auth.code");
            live.TokenUpdated += delegate(object sender, EventArgs e) {
                cfg.Set ("onedrive.auth.access-token", live.AccessToken);
                cfg.Set ("onedrive.auth.refresh-token", live.RefreshToken);
                cfg.Set ("onedrive.auth.expiration", live.Expires.ToString ("O"));
                cfg.Save ();
            };

            string liveCode = cfg.Get ("onedrive.auth.code", null);
            live.AccessToken = cfg.Get ("onedrive.auth.access-token", null);
            live.RefreshToken = cfg.Get ("onedrive.auth.refresh-token", null);
            DateTime expiration;
            if (DateTime.TryParseExact (
                    cfg.Get ("onedrive.auth.expiration", ""),
                    "O",
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out expiration)) {
                live.Expires = expiration;
            }

            try {
                if (!string.IsNullOrEmpty(liveCode)) {
                    live.Authorize (liveCode);
                }
                if (!live.ValidateAccessToken ()) {
                    live.RefreshAccessToken ();
                }
            } catch {
                try {
                    Process.Start (live.LoginUrl);
                } catch {
                    Console.WriteLine (live.LoginUrl);
                }
                Console.Write ("input code: ");
                cfg.Set ("onedrive.auth.code", Console.ReadLine ().Trim ());
                cfg.Save ();
                goto Retry;
            }

            cfg.Save ();
            return live;
        }

        #if false
        static void Main (string[] args)
        {
            string confDir = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.ApplicationData), "EncryptedOneDrive");
            if (!Directory.Exists (confDir))
                Directory.CreateDirectory (confDir);

            string tokenFile = Path.Combine (confDir, "token.txt");
            if (!File.Exists (tokenFile))
                throw new FileNotFoundException (tokenFile);
            string token;
            using (StreamReader reader = new StreamReader (tokenFile)) {
                token = reader.ReadLine ().Trim ();
            }

            OneDriveClient oneDriveClient = new OneDriveClient (token);
            CryptoManager cryptoMgr = new CryptoManager ("password", "encrypted-userspace-filesystem-over-onedrive");
            using (FileSystem fs = new FileSystem (oneDriveClient, cryptoMgr)) {
                if (Environment.OSVersion.Platform == PlatformID.Unix) {
                    // Mono-FUSE
                    using (Fuse fuse = new Fuse (fs)) {
                        args = fuse.ParseFuseArguments (args);
                        fuse.MountPoint = "/mnt/fuse";
                        fuse.Start();
                    }
                } else {
                    // Dokan.NET (TODO)
                }
            }
        }
        #endif
    }
}
