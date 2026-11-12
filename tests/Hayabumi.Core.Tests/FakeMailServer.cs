using System.Text;
using Hayabumi.Core.Accounts;
using Hayabumi.Core.Storage;
using Hayabumi.Core.Sync;

namespace Hayabumi.Core.Tests;

/// <summary>同期処理のテスト用に、メモリ上で IMAP サーバーの振る舞いを再現する。</summary>
internal sealed class FakeMailServer : IMailStoreSessionFactory
{
    public Dictionary<string, FakeFolder> Folders { get; } = new(StringComparer.Ordinal);

    public int ConnectCount { get; private set; }

    public bool FailConnect { get; set; }

    public FakeFolder AddFolder(string fullName, FolderRole role = FolderRole.None, bool supportsModSeq = false)
    {
        var folder = new FakeFolder(fullName, role, supportsModSeq);
        Folders[fullName] = folder;
        return folder;
    }

    public Task<IMailStoreSession> ConnectAsync(Account account, CancellationToken cancellationToken)
    {
        if (FailConnect)
        {
            throw new IOException("接続できません");
        }

        ConnectCount++;
        return Task.FromResult<IMailStoreSession>(new Session(this));
    }

    private sealed class Session(FakeMailServer server) : IMailStoreSession
    {
        public Task<IReadOnlyList<RemoteFolderInfo>> GetFoldersAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RemoteFolderInfo>>(
                [.. server.Folders.Values.Select(f => new RemoteFolderInfo(f.FullName, f.FullName, '/', f.Role, f.CanSelect))]);

        public Task<IRemoteFolder> OpenFolderAsync(string fullName, bool writable, CancellationToken cancellationToken)
            => Task.FromResult<IRemoteFolder>(new Opened(server, server.Folders[fullName]));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Opened(FakeMailServer server, FakeFolder folder) : IRemoteFolder
    {
        public string FullName => folder.FullName;
        public uint UidValidity => folder.UidValidity;
        public ulong? HighestModSeq => folder.SupportsModSeq ? folder.ModSeq : null;
        public int Count => folder.Messages.Count;

        public Task<IReadOnlyList<uint>> GetUidsAsync(uint minUid, CancellationToken cancellationToken)
        {
            var uids = folder.Messages.Keys.Where(u => u >= minUid).Order().ToList();
            // IMAP の「n:*」は、n より大きい UID がなくても最後のメッセージを返す
            if (uids.Count == 0 && folder.Messages.Count > 0)
            {
                uids.Add(folder.Messages.Keys.Max());
            }

            return Task.FromResult<IReadOnlyList<uint>>(uids);
        }

        public Task<IReadOnlyList<NewMessage>> FetchHeadersAsync(IReadOnlyList<uint> uids, bool includePreview, CancellationToken cancellationToken)
        {
            folder.HeaderFetches.Add(uids.ToList());
            return Task.FromResult<IReadOnlyList<NewMessage>>(
                [.. uids.Where(folder.Messages.ContainsKey).Select(u => folder.Messages[u].Header with { Uid = u, Flags = folder.Messages[u].Flags })]);
        }

        public Task<IReadOnlyList<RemoteFlags>> FetchFlagsAsync(IReadOnlyList<uint> uids, ulong? changedSince, CancellationToken cancellationToken)
        {
            folder.FlagFetches.Add(changedSince);
            return Task.FromResult<IReadOnlyList<RemoteFlags>>(
                [.. uids.Where(folder.Messages.ContainsKey)
                    .Select(u => (Uid: u, Message: folder.Messages[u]))
                    .Where(x => changedSince is null || x.Message.ModSeq > changedSince)
                    .Select(x => new RemoteFlags(x.Uid, x.Message.Flags, [], x.Message.ModSeq))]);
        }

        public async Task CopyMessageToAsync(uint uid, Stream destination, CancellationToken cancellationToken)
        {
            if (!folder.Messages.TryGetValue(uid, out var message))
            {
                throw new RemoteMessageNotFoundException("not found");
            }

            await destination.WriteAsync(message.Raw, cancellationToken);
        }

        public Task SetFlagsAsync(IReadOnlyList<uint> uids, MessageFlags flags, bool add, CancellationToken cancellationToken)
        {
            foreach (var uid in uids.Where(folder.Messages.ContainsKey))
            {
                var m = folder.Messages[uid];
                m.Flags = add ? m.Flags | flags : m.Flags & ~flags;
                m.ModSeq = ++folder.ModSeq;
            }

            return Task.CompletedTask;
        }

        public Task MoveToAsync(IReadOnlyList<uint> uids, string destinationFullName, CancellationToken cancellationToken)
        {
            var destination = server.Folders[destinationFullName];
            foreach (var uid in uids.Where(folder.Messages.ContainsKey))
            {
                destination.Add(folder.Messages[uid]);
                folder.Messages.Remove(uid);
            }

            return Task.CompletedTask;
        }

        public Task DeletePermanentlyAsync(IReadOnlyList<uint> uids, CancellationToken cancellationToken)
        {
            foreach (var uid in uids)
            {
                folder.Messages.Remove(uid);
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FakeMessage(NewMessage header, byte[] raw)
{
    public NewMessage Header { get; } = header;
    public byte[] Raw { get; } = raw;
    public MessageFlags Flags { get; set; } = header.Flags;
    public ulong ModSeq { get; set; }

    public static FakeMessage Create(string subject, string body = "本文", int minutesAgo = 0)
    {
        var date = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);
        var raw = Encoding.UTF8.GetBytes(
            $"From: sender@example.jp\r\nSubject: =?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(subject))}?=\r\nDate: {date:r}\r\n" +
            $"Content-Type: text/plain; charset=UTF-8\r\n\r\n{body}\r\n");
        return new FakeMessage(new NewMessage { Subject = subject, FromAddress = "sender@example.jp", Date = date, Size = raw.Length }, raw);
    }
}

internal sealed class FakeFolder(string fullName, FolderRole role, bool supportsModSeq)
{
    public string FullName { get; } = fullName;
    public FolderRole Role { get; } = role;
    public bool SupportsModSeq { get; } = supportsModSeq;
    public bool CanSelect { get; set; } = true;
    public uint UidValidity { get; set; } = 1000;
    public uint NextUid { get; set; } = 1;
    public ulong ModSeq { get; set; } = 1;
    public SortedDictionary<uint, FakeMessage> Messages { get; } = [];
    public List<List<uint>> HeaderFetches { get; } = [];
    public List<ulong?> FlagFetches { get; } = [];

    public uint Add(FakeMessage message)
    {
        var uid = NextUid++;
        message.ModSeq = ++ModSeq;
        Messages[uid] = message;
        return uid;
    }

    public void SetFlags(uint uid, MessageFlags flags)
    {
        Messages[uid].Flags = flags;
        Messages[uid].ModSeq = ++ModSeq;
    }
}
