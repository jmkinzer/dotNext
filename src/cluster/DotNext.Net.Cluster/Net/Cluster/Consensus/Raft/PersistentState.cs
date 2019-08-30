using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipelines;
using System.Net;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using static System.Text.Encoding;
using static System.Globalization.CultureInfo;

namespace DotNext.Net.Cluster.Consensus.Raft
{
    using Messaging;
    using Threading;
    using CommitEventHandler = Replication.CommitEventHandler<ILogEntry>;
    using IAuditTrail = Replication.IAuditTrail<ILogEntry>;

    public sealed class PersistentState : Disposable, IPersistentState
    {
        private const byte EmptyContentIndicator = 0;

        private sealed class LogEntry : ILogEntry
        {
            private readonly MemoryMappedFile mappedFile;
            private readonly long contentOffset;
            internal readonly long Length;

            private LogEntry(MemoryMappedFile mappedFile, string name, string type, long term, long length, long contentOffset)
            {
                this.mappedFile = mappedFile;
                this.contentOffset = contentOffset;
                Length = length;
                Term = term;
                Type = new ContentType(type);
                Name = name;
            }

            internal static LogEntry Create(MemoryMappedFile mappedFile, long offset, long maxRecordSize)
            {
                using (var reader = new BinaryReader(mappedFile.CreateViewStream(offset, maxRecordSize, MemoryMappedFileAccess.Read), UTF8, false))
                    return reader.ReadByte() == EmptyContentIndicator ?
                        null :
                        new LogEntry(mappedFile, reader.ReadString(), reader.ReadString(), reader.ReadInt64(), reader.ReadInt64(), reader.BaseStream.Position + offset);

            }

            internal static async Task WriteAsync(ILogEntry entry, MemoryMappedFile output, long offset, long maxRecordSize)
            {
                using (var content = output.CreateViewStream(offset, maxRecordSize))
                using (var writer = new BinaryWriter(content, UTF8, true))
                {
                    //write metadata
                    writer.Write((byte)1);    //indicates that content is not empty
                    writer.Write(entry.Name);
                    writer.Write(entry.Type.ToString());
                    writer.Write(entry.Term);
                    var lengthPos = content.Position;    //remember position of length value
                    writer.Write(0L);
                    //write content
                    await entry.CopyToAsync(content).ConfigureAwait(false);
                    //write length
                    var length = content.Position - lengthPos - sizeof(long);
                    content.Position = lengthPos;
                    writer.Write(length);
                    await content.FlushAsync().ConfigureAwait(false);
                }
            }

            async Task IMessage.CopyToAsync(Stream output)
            {
                using(var content = mappedFile.CreateViewStream(contentOffset, Length, MemoryMappedFileAccess.Read))
                    await content.CopyToAsync(output).ConfigureAwait(false);
            }
            
            async ValueTask IMessage.CopyToAsync(PipeWriter output, CancellationToken token)
            {
                using(var content = mappedFile.CreateViewStream(contentOffset, Length, MemoryMappedFileAccess.Read))
                    await StreamMessage.CopyToAsync(content, output, token, false).ConfigureAwait(false);
            }

            long? IMessage.Length => Length;
            bool IMessage.IsReusable => true;

            public string Name { get; }

            public ContentType Type { get; }

            public long Term { get; }
        }

        /*
            Partition file format:
            FileName - number of partition
            OF 8 bytes = record index offset
            C  8 bytes = number of committed entries
            Payload:
            [metadata, length = 8 bytes, content] X N
         */
        private sealed class Partition : Disposable
        {
            private const long RecordIndexOffsetOffset = 0;
            private const long CommittedEntriesOffset = RecordIndexOffsetOffset + sizeof(long);
            private const long PayloadOffset = CommittedEntriesOffset + sizeof(long);

            private readonly MemoryMappedFile mappedFile;
            private readonly MemoryMappedViewAccessor headersView;
            private readonly long maxRecordSize;

            internal Partition(string fileName, long recordsPerPartition, long maxRecordSize)
            {
                Capacity = recordsPerPartition;
                this.maxRecordSize = maxRecordSize;
                mappedFile = MemoryMappedFile.CreateFromFile(fileName, FileMode.OpenOrCreate, null, PayloadOffset + recordsPerPartition * maxRecordSize, MemoryMappedFileAccess.ReadWrite);
                headersView = mappedFile.CreateViewAccessor(0L, PayloadOffset, MemoryMappedFileAccess.ReadWrite);
            }

            internal Partition(string fileName, long recordsPerPartition, long maxRecordSize, long partitionNumber)
                : this(fileName, recordsPerPartition, maxRecordSize)
            {
                headersView.Write(RecordIndexOffsetOffset, partitionNumber * recordsPerPartition);
            }

            internal void FlushHeaders() => headersView.Flush();

            internal long IndexOffset
            {
                get => headersView.ReadInt64(RecordIndexOffsetOffset);
            }

            internal long CommittedEntries
            {
                get => headersView.ReadInt64(CommittedEntriesOffset);
                set => headersView.Write(CommittedEntries, value);
            }

            //max number of entries
            internal long Capacity { get; }

            //current count of entries
            internal long Count
            {
                get
                {
                    var result = IndexOffset == 0L ? 1L : 0L;
                    while (result < Capacity)
                    {
                        var offset = PayloadOffset + result * maxRecordSize;
                        if (headersView.ReadByte(offset) == EmptyContentIndicator)
                            break;
                        result += 1L;
                    }
                    return result;
                }
            }

            internal ILogEntry this[long index]
            {
                get
                {
                    var offset = IndexOffset;
                    if(index == 0L && offset == 0L)
                        return First;
                    index -= offset;
                    offset = PayloadOffset + index * maxRecordSize;
                    return LogEntry.Create(mappedFile, offset, maxRecordSize);
                }
            }

            internal Task WriteAsync(ILogEntry entry, long index)
                => LogEntry.WriteAsync(entry, mappedFile, PayloadOffset + index * maxRecordSize, maxRecordSize);

            protected override void Dispose(bool disposing)
            {
                if(disposing)
                {
                    headersView.Dispose();
                    mappedFile.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        /*
            State file format:
            8 bytes = Term
            4 bytes = Node port
            4 bytes = Address Length
            octet string = IP Address
         */
        private sealed class NodeState : Disposable
        {
            internal const string FileName = ".state";
            private const long Capacity = 1024; //1 KB
            private const long TermOffset = 0L;
            private const long PortOffset = TermOffset + sizeof(long);
            private const long AddressLengthOffset = PortOffset + sizeof(int);
            private const long AddressOffset = AddressLengthOffset + sizeof(int);
            private readonly MemoryMappedFile mappedFile;
            private readonly MemoryMappedViewAccessor stateView;
            private AsyncLock syncRoot; //node state has separated 
            private volatile IPEndPoint votedFor;
            private long term;  //volatile

            internal NodeState(string fileName, AsyncLock writeLock)
            {
                mappedFile = MemoryMappedFile.CreateFromFile(fileName, FileMode.OpenOrCreate, null, Capacity, MemoryMappedFileAccess.ReadWrite);
                syncRoot = writeLock;
                stateView = mappedFile.CreateViewAccessor();
                term = stateView.ReadInt64(TermOffset);
                var port = stateView.ReadInt32(PortOffset);
                var length = stateView.ReadInt32(AddressLengthOffset);
                if(length == 0)
                    votedFor = null;
                else
                {
                    var address = new byte[length];
                    stateView.ReadArray(AddressOffset, address, 0, length);
                    votedFor = new IPEndPoint(new IPAddress(address), port);
                }
            }

            internal long Term => term.VolatileRead();

            internal async ValueTask UpdateTermAsync(long value)
            {
                using(await syncRoot.Acquire(CancellationToken.None).ConfigureAwait(false))
                {
                    stateView.Write(TermOffset, value);
                    stateView.Flush();
                    term.VolatileWrite(value);
                }
            }

            internal async ValueTask<long> IncrementTermAsync()
            {
                using(await syncRoot.Acquire(CancellationToken.None).ConfigureAwait(false))
                {
                    var result = term.IncrementAndGet();
                    stateView.Write(TermOffset, result);
                    stateView.Flush();
                    return result;
                }
            }

            internal bool IsVotedFor(IPEndPoint member)
            {
                var lastVote = votedFor;
                return lastVote is null || Equals(lastVote, member);
            }

            internal async ValueTask UpdateVotedForAsync(IPEndPoint member)
            {
                using(await syncRoot.Acquire(CancellationToken.None).ConfigureAwait(false))
                {
                    if(member is null)
                    {
                        stateView.Write(PortOffset, 0);
                        stateView.Write(AddressLengthOffset, 0);
                    }
                    else
                    {
                        stateView.Write(PortOffset, member.Port);
                        var address = member.Address.GetAddressBytes();
                        stateView.Write(AddressLengthOffset, address.Length);
                        stateView.WriteArray(AddressOffset, address, 0, address.Length);
                    }
                    stateView.Flush();
                    votedFor = member;
                }
            }

            protected override void Dispose(bool disposing)
            {
                if(disposing)
                {
                    stateView.Dispose();
                    mappedFile.Dispose();
                    syncRoot.Dispose();
                    votedFor = null;
                }
                base.Dispose(disposing);
            }
        }

        private static readonly ValueFunc<long, long, long> MaxFunc = new ValueFunc<long, long, long>(Math.Max);
        private long commitIndex, lastIndex;
        private readonly long recordsPerPartition;
        private readonly long maxRecordSize;
        private readonly AsyncReaderWriterLock syncRoot;
        //key is the number of partition
        private readonly Dictionary<long, Partition> partitionTable;
        private readonly NodeState state;
        private readonly DirectoryInfo location;

        public PersistentState(DirectoryInfo location, long recordsPerPartition, long maxRecordSize)
        {
            if(!location.Exists)
                location.Create();
            this.location = location;
            this.recordsPerPartition = recordsPerPartition;
            this.maxRecordSize = maxRecordSize;
            syncRoot = new AsyncReaderWriterLock();
            partitionTable = new Dictionary<long, Partition>();
            //load all partitions from file system
            foreach(var file in location.EnumerateFiles())
                if(long.TryParse(file.Name, out var partitionNumber))
                {
                    var partition = new Partition(file.FullName, recordsPerPartition, maxRecordSize);
                    commitIndex += partition.CommittedEntries;
                    lastIndex += partition.Count;
                    partitionTable[partitionNumber] = partition;
                }
            //load node state
            state = new NodeState(Path.Combine(location.FullName, NodeState.FileName), AsyncLock.WriteLock(syncRoot));
        }

        public PersistentState(string path, long recordsPerPartition, long maxRecordSize)
            : this(new DirectoryInfo(path), recordsPerPartition, maxRecordSize)
        {
        }


        /// <summary>
        /// Gets index of the committed or last log entry.
        /// </summary>
        /// <remarks>
        /// This method is synchronous because returning value should be cached and updated in memory by implementing class.
        /// </remarks>
        /// <param name="committed"><see langword="true"/> to get the index of highest log entry known to be committed; <see langword="false"/> to get the index of the last log entry.</param>
        /// <returns>The index of the log entry.</returns>
        public long GetLastIndex(bool committed) => committed ? commitIndex.VolatileRead() : lastIndex.VolatileRead();

        private long PartitionOf(long recordIndex) => recordIndex / recordsPerPartition;

        private bool TryGetPartition(long recordIndex, out Partition partition)
            => partitionTable.TryGetValue(PartitionOf(recordIndex), out partition);
        
        private Partition GetOrCreatePartition(long recordIndex)
        {
            var partitionNumber = PartitionOf(recordIndex);
            if (!partitionTable.TryGetValue(partitionNumber, out var partition))
            {
                partition = new Partition(Path.Combine(location.FullName, partitionNumber.ToString(InvariantCulture)), recordsPerPartition, maxRecordSize, partitionNumber);
                partition.FlushHeaders();
                partitionTable.Add(partitionNumber, partition);
            }
            return partition;
        }

        private IReadOnlyList<ILogEntry> GetEntries(long startIndex, long endIndex)
        {
            if(endIndex < startIndex)
                return Array.Empty<LogEntry>();
            if(partitionTable.Count == 0)
                return startIndex == 0L ? InMemoryAuditTrail.EmptyLog : Array.Empty<LogEntry>();

            var result = new ILogEntry[endIndex - startIndex + 1L];
            for(var i = 0L; startIndex <= endIndex; startIndex++, i++)
            {
                ILogEntry entry;
                if(TryGetPartition(startIndex, out var partition) && (entry = partition[startIndex]) != null)
                    result[i] = entry;
                else
                {
                    result = result.RemoveLast(result.LongLength - i);
                    break;
                }
            }
            return result;
        }
        
        async ValueTask<IReadOnlyList<ILogEntry>> IAuditTrail.GetEntriesAsync(long startIndex, long? endIndex)
        {
            using(await syncRoot.AcquireReadLockAsync(CancellationToken.None).ConfigureAwait(false))
                return GetEntries(startIndex, endIndex ?? lastIndex);
        }

        private ValueTask AppendAsync(ILogEntry entry, long index)
            => new ValueTask(GetOrCreatePartition(index).WriteAsync(entry, index));

        private async ValueTask<long> AppendAsync(IReadOnlyList<ILogEntry> entries, long startIndex)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var idx = startIndex + i;
                lastIndex.AccumulateAndGet(idx, in MaxFunc);
                await AppendAsync(entries[i], idx).ConfigureAwait(false);
            }
            return startIndex;
        }

        async ValueTask<long> IAuditTrail.AppendAsync(IReadOnlyList<ILogEntry> entries, long? startIndex)
        {
            if (entries.Count == 0)
                throw new ArgumentException(ExceptionMessages.EntrySetIsEmpty, nameof(entries));
            using(await syncRoot.AcquireWriteLockAsync(CancellationToken.None).ConfigureAwait(false))
                return await AppendAsync(entries, startIndex ?? lastIndex + 1L).ConfigureAwait(false);
        }

        /// <summary>
        /// The event that is raised when actual commit happen.
        /// </summary>
        public event CommitEventHandler Committed;

        ValueTask<long> IAuditTrail.CommitAsync(long? endIndex)
        {
            throw new NotImplementedException();
        }

        private static ref readonly ILogEntry First => ref InMemoryAuditTrail.EmptyLog[0];

        ref readonly ILogEntry IAuditTrail.First => ref First;

        /// <summary>
        /// Forces log compaction.
        /// </summary>
        /// <remarks>
        /// Log compaction allows to remove committed log entries from the log and reduce its size.
        /// </remarks>
        /// <returns>The number of removed entries.</returns>
        public ValueTask<long> ForceCompactionAsync()
        {
            throw new NotImplementedException();
        }

        bool IPersistentState.IsVotedFor(IRaftClusterMember member) => state.IsVotedFor(member?.Endpoint);

        long IPersistentState.Term => state.Term;

        ValueTask<long> IPersistentState.IncrementTermAsync() => state.IncrementTermAsync();

        ValueTask IPersistentState.UpdateTermAsync(long term) => state.UpdateTermAsync(term);

        ValueTask IPersistentState.UpdateVotedForAsync(IRaftClusterMember member) => state.UpdateVotedForAsync(member?.Endpoint);

        protected override void Dispose(bool disposing)
        {
            if(disposing)
            {
                syncRoot.Dispose();
                foreach(var partition in partitionTable.Values)
                    partition.Dispose();
                partitionTable.Clear();
                state.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}