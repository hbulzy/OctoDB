using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using OctoDB.Diagnostics;
using OctoDB.Storage;

namespace OctoDB.Tests.Fixtures
{
    public class StorageFixture
    {
        protected IStorageEngine Storage { get; set; }
        protected Store Store { get; set; }

        [SetUp]
        public void SetUp()
        {
            var path = Path.Combine(Environment.CurrentDirectory, GetType().Name + "Test");
            EnsurePath(path);

            Store = new Store(path);
            Storage = Store.StorageEngine;
        }

        [TearDown]
        public void TearDown()
        {
            Store.Statistics.Print();

            if (Storage != null)
            {
                Storage.Dispose();
            }
        }

        static void EnsurePath(string path)
        {
            Trace.WriteLine("Test data folder: " + path);

            var directory = new DirectoryInfo(path);
            if (directory.Exists)
            {
                DeleteFileSystemInfo(directory);
            }
            directory.Create();
        }

        private static void DeleteFileSystemInfo(FileSystemInfo fileSystemInfo)
        {
            var directoryInfo = fileSystemInfo as DirectoryInfo;
            if (directoryInfo != null)
            {
                foreach (var childInfo in directoryInfo.GetFileSystemInfos())
                {
                    DeleteFileSystemInfo(childInfo);
                }
            }

            fileSystemInfo.Attributes = FileAttributes.Normal;
            fileSystemInfo.Delete();
        }
    }
}