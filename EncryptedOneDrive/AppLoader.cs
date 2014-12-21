using System;
using System.IO;

namespace EncryptedOneDrive
{
    public class AppLoader
    {
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
    }
}
