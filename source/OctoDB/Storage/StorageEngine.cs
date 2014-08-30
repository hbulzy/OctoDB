using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LibGit2Sharp;
using OctoDB.Diagnostics;

namespace OctoDB.Storage
{
    public class StorageEngine : IStorageEngine
    {
        readonly Repository repository;
        readonly ReaderWriterLockSlim sync = new ReaderWriterLockSlim();
        readonly IStatistics statistics;
        
        public StorageEngine(string rootPath, IStatistics statistics)
        {
            this.statistics = statistics;
            Repository.Init(rootPath);
            repository = new Repository(rootPath);
        }

        public IStatistics Statistics { get { return statistics; } }

        public bool IsRepositoryEmpty
        {
            get
            {
                var anchor = GetCurrentAnchor();
                return anchor.Id == null;
            }
        }

        public IAnchor GetCurrentAnchor()
        {
            sync.EnterReadLock();
            try
            {
                var tip = repository.Head.Tip;
                return new GitSnapshotAnchor(tip);
            }
            finally
            {
                sync.ExitReadLock();
            }
        }

        public void Visit(IAnchor anchor, IStorageVisitor visitorloaded)
        {
            var gitReference = anchor as GitSnapshotAnchor;
            if (gitReference == null) throw new InvalidOperationException("Not a valid time reference");
            if (gitReference.Commit == null)
            {
                return;
            }

            sync.EnterReadLock();
            try
            {
                var tree = gitReference.Commit.Tree;
                LoadTree("", tree, visitorloaded);
            }
            finally
            {
                sync.ExitReadLock();
            }
        }

        void LoadTree(string parentPath, Tree tree, IStorageVisitor visitor)
        {
            foreach (var entry in tree)
            {
                if (entry.TargetType == TreeEntryTargetType.Tree)
                {
                    var child = entry.Target as Tree;
                    if (child != null)
                    {
                        if (visitor.ShouldTraverseDirectory(entry.Path))
                        {
                            LoadTree(entry.Path, child, visitor);
                        }
                    }
                }
            }

            visitor.VisitDirectory(parentPath, 
                (from entry in tree
                where entry.TargetType == TreeEntryTargetType.Blob
                let blob = entry.Target as Blob
                where blob != null
                select new StoredFile(entry.Name, entry.Path, blob.Sha, () => blob.GetContentStream(new FilteringOptions(entry.Path)))).ToList()
                );
        }

        public IStorageBatch Batch()
        {
            sync.EnterWriteLock();
            {
                var newBatch = new StorageBatch(repository, statistics, delegate
                {
                    sync.ExitWriteLock();
                });
                newBatch.Prepare();
                return newBatch;
            }
        }

        public void Dispose()
        {
            sync.EnterWriteLock();
            try
            {
                repository.Dispose();
            }
            finally
            {
                sync.ExitWriteLock();
            }
        }

        class StorageBatch : IStorageBatch
        {
            private readonly Repository repository;
            readonly IStatistics statistics;
            private readonly Action released;
            TreeDefinition treeDefinition;
            Commit parent;
            readonly List<string> pendingStages = new List<string>();
            readonly List<string> pendingDeletes = new List<string>();

            public StorageBatch(Repository repository, IStatistics statistics, Action released)
            {
                this.repository = repository;
                this.statistics = statistics;
                this.released = released;
            }

            public void Prepare()
            {
                parent = repository.Head.Tip;
                if (parent == null)
                {
                    treeDefinition = new TreeDefinition();
                }
                else
                {
                    treeDefinition = TreeDefinition.From(parent.Tree);
                }

                GitReset();
            }

            void GitReset()
            {
                //if (repository.Head.Tip != null)
                //{
                //    using (statistics.MeasureGitReset())
                //    {
                //        repository.Reset(ResetMode.Hard);
                //    }
                //}
            }

            public void Put(string path, Action<Stream> streamWriter)
            {
                using (var stream = new MemoryStream())
                {
                    streamWriter(new NonClosableStream(stream));

                    using (statistics.MeasureGitStaging())
                    {
                        stream.Seek(0, SeekOrigin.Begin);
                        var blob = repository.ObjectDatabase.CreateBlob(stream, path);

                        treeDefinition.Add(path, blob, Mode.NonExecutableFile);
                    }
                }
            }

            public void Delete(string path)
            {
                using (statistics.MeasureGitStaging())
                {
                    treeDefinition.Remove(path);
                }
            }

            public void Commit(string message)
            {
                using (statistics.MeasureGitCommit())
                {
                    var tree = repository.ObjectDatabase.CreateTree(treeDefinition);

                    var parents = parent == null ? new Commit[0] : new[] { parent };

                    var commit = repository.ObjectDatabase.CreateCommit(
                        new Signature("paul", "paul@paulstovell.com", DateTimeOffset.UtcNow),
                        new Signature("paul", "paul@paulstovell.com", DateTimeOffset.UtcNow),
                        message,
                        tree,
                        parents,
                        false);

                    repository.Refs.UpdateTarget(repository.Refs.Head, commit.Id);

                    var masterBranch = repository.Branches.FirstOrDefault(b => b.Name == "master");
                    if (masterBranch == null)
                    {
                        masterBranch = repository.CreateBranch("master", commit);
                        repository.Checkout(masterBranch, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
                    }

                    repository.Refs.UpdateTarget(repository.Refs["refs/heads/master"], commit.Id);

                    using (statistics.MeasureGitReset())
                    {
                        repository.Reset(ResetMode.Hard, commit);
                    }
                }
            }

            public void Dispose()
            {
                if (released != null) released();
            }
        }

        class GitSnapshotAnchor : IAnchor
        {
            readonly Commit commit;

            public GitSnapshotAnchor(Commit commit)
            {
                this.commit = commit;
                if (commit != null)
                {
                    Message = commit.Message;
                    Author = new CommitSignature(commit.Author.Name, commit.Author.Email, commit.Author.When);
                    Committer = new CommitSignature(commit.Committer.Name, commit.Committer.Email, commit.Committer.When);                    
                }
            }

            public string Id { get { return Commit == null ? "" : Commit.Sha; } }
            public string Message { get; private set; }
            public CommitSignature Author { get; private set; }
            public CommitSignature Committer { get; private set; }
            internal Commit Commit { get { return commit; } }
        }
    }
}