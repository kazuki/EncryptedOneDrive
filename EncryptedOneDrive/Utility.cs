using System;
using System.Threading;

namespace EncryptedOneDrive
{
    public static class Utility
    {
        public static IDisposable ReadLock (this ReaderWriterLockSlim l)
        {
            l.EnterReadLock ();
            return new LockState (l, 0);
        }

        public static IDisposable UpgradeableReadLock (this ReaderWriterLockSlim l)
        {
            l.EnterUpgradeableReadLock ();
            return new LockState (l, 1);
        }

        public static IDisposable WriteLock (this ReaderWriterLockSlim l)
        {
            l.EnterWriteLock ();
            return new LockState (l, 2);
        }

        class LockState : IDisposable
        {
            ReaderWriterLockSlim _rwl;
            readonly int _type;

            public LockState (ReaderWriterLockSlim rwl, int type)
            {
                _rwl = rwl;
                _type = type;
            }

            public void Dispose ()
            {
                if (_rwl != null) {
                    switch (_type) {
                    case 0:
                        _rwl.ExitReadLock ();
                        break;
                    case 1:
                        _rwl.ExitUpgradeableReadLock ();
                        break;
                    case 2:
                        _rwl.ExitWriteLock ();
                        break;
                    }
                    _rwl = null;
                }
            }
        }
    }
}
