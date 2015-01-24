using DB = global::LevelDB.DB;
using Native = global::LevelDB.Native;
using ReadOptions = global::LevelDB.ReadOptions;
using WriteOptions = global::LevelDB.WriteOptions;

namespace EncryptedOneDrive
{
    public class LevelDB : IKeyValueStore
    {
        DB db = null;

        public LevelDB ()
        {
        }

        public void Opne (string path)
        {
            var opt = new global::LevelDB.Options ();
            opt.CreateIfMissing = true;
            db = DB.Open (opt, path);
        }

        public byte[] Get (string key)
        {
            return db.GetRaw (key);
        }

        public void Put (string key, byte[] value)
        {
            db.Put (key, value);
        }

        public void Delete (string key)
        {
            db.Delete (key);
        }

        public void Dispose ()
        {
            if (db != null) {
                db.Dispose ();
                db = null;
            }
        }
    }
}
