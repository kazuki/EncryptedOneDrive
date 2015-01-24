using NUnit.Framework;
using EncryptedOneDrive;

namespace EncryptedOneDrive.Tests
{
    [TestFixture ()]
    public class OneDriveStorageTest
    {
        OneDrive.FileSystem fs = null;

        [TestFixtureSetUp]
        public void Setup()
        {
            var cfg = new Config ();
            var auth = AppLoader.Login (cfg);
            fs = new OneDrive.FileSystem (
                cfg,
                new OneDrive.RestClient(auth),
                TestUtility.DummyKVS.Instance);
        }

        [TestFixtureTearDown]
        public void Teardown()
        {
            if (fs != null) {
                fs.Dispose ();
                fs = null;
            }
        }

        [Test ()]
        public void TestCase ()
        {
        }
    }
}
