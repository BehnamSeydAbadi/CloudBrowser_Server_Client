using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Streams;

namespace BrowserClient
{
    /// <summary>
    /// Receives FILE chunks from the server, writes them into the Windows Downloads folder,
    /// and keeps a local index for the Downloads UI.
    /// </summary>
    public sealed class DownloadStore
    {
        private const string IndexFileName = "downloads_index.json";
        private const byte FlagLast = 1;

        private readonly object sync = new object();
        private readonly Dictionary<string, DownloadInfo> items = new Dictionary<string, DownloadInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IncomingTransfer> incoming = new Dictionary<string, IncomingTransfer>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> inboundFilePackets =
            new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        private int fileWriterRunning;
        private bool loaded;

        public event EventHandler ListChanged;

        /// <summary>Invoked after a FILE chunk is safely written (id, seq).</summary>
        public Action<string, int> OnChunkWritten;

        /// <summary>
        /// Queue a FILE packet for background disk write so the WebSocket receive loop never blocks.
        /// </summary>
        public void EnqueueFilePacket(byte[] packet)
        {
            if (packet == null || packet.Length < 4)
                return;

            // Server ACK window keeps this queue small (~window size); do not block the WS receive loop.
            inboundFilePackets.Enqueue(packet);
            if (System.Threading.Interlocked.CompareExchange(ref fileWriterRunning, 1, 0) == 0)
            {
                var ignored = Task.Run((Func<Task>)FileWriterLoopAsync);
            }
        }

        private async Task FileWriterLoopAsync()
        {
            try
            {
                while (true)
                {
                    byte[] packet;
                    if (!inboundFilePackets.TryDequeue(out packet))
                    {
                        await Task.Delay(20);
                        if (!inboundFilePackets.TryDequeue(out packet))
                        {
                            System.Threading.Interlocked.Exchange(ref fileWriterRunning, 0);
                            // Race: packet arrived after empty check.
                            if (!inboundFilePackets.IsEmpty &&
                                System.Threading.Interlocked.CompareExchange(ref fileWriterRunning, 1, 0) == 0)
                            {
                                continue;
                            }
                            return;
                        }
                    }

                    try
                    {
                        await HandleFilePacketAsync(packet, packet.Length);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
                System.Threading.Interlocked.Exchange(ref fileWriterRunning, 0);
            }
        }

        public async Task EnsureLoadedAsync()
        {
            if (loaded)
                return;

            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(IndexFileName, CreationCollisionOption.OpenIfExists);
                var json = await FileIO.ReadTextAsync(file);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var list = JsonConvert.DeserializeObject<List<DownloadInfo>>(json) ?? new List<DownloadInfo>();
                    lock (sync)
                    {
                        items.Clear();
                        foreach (var item in list)
                        {
                            if (item == null || string.IsNullOrEmpty(item.id))
                                continue;
                            items[item.id] = item;
                        }
                    }
                }
            }
            catch
            {
            }

            loaded = true;
            // Do not raise ListChanged on cold load — avoids toasting every past download.
        }

        public List<DownloadInfo> GetSnapshot()
        {
            lock (sync)
            {
                return items.Values
                    .OrderByDescending(i => i.completedUtc ?? "")
                    .ThenByDescending(i => i.fileName)
                    .Select(Clone)
                    .ToList();
            }
        }

        public void OnStarted(DownloadEventPayload payload)
        {
            if (payload == null || string.IsNullOrEmpty(payload.id))
                return;

            lock (sync)
            {
                items[payload.id] = new DownloadInfo
                {
                    id = payload.id,
                    fileName = string.IsNullOrWhiteSpace(payload.fileName) ? "download" : payload.fileName,
                    mimeType = payload.mimeType,
                    size = payload.totalBytes,
                    status = "downloading",
                    percent = Math.Max(0, payload.percent),
                    error = null
                };
            }
            RaiseListChanged();
        }

        public void OnProgress(DownloadEventPayload payload)
        {
            if (payload == null || string.IsNullOrEmpty(payload.id))
                return;

            lock (sync)
            {
                DownloadInfo info;
                if (!items.TryGetValue(payload.id, out info))
                {
                    info = new DownloadInfo { id = payload.id };
                    items[payload.id] = info;
                }

                if (!string.IsNullOrWhiteSpace(payload.fileName))
                    info.fileName = payload.fileName;
                if (!string.IsNullOrWhiteSpace(payload.mimeType))
                    info.mimeType = payload.mimeType;
                info.size = payload.totalBytes > 0 ? payload.totalBytes : info.size;
                info.percent = Math.Max(0, Math.Min(100, payload.percent));
                if (info.status != "completed" && info.status != "failed")
                    info.status = info.percent >= 100 ? "transferring" : "downloading";
            }
            RaiseListChanged();
        }

        public void OnCompletedMeta(DownloadEventPayload payload)
        {
            if (payload == null || string.IsNullOrEmpty(payload.id))
                return;

            if (!payload.success)
            {
                lock (sync)
                {
                    DownloadInfo info;
                    if (!items.TryGetValue(payload.id, out info))
                    {
                        info = new DownloadInfo { id = payload.id, fileName = payload.fileName ?? "download" };
                        items[payload.id] = info;
                    }
                    info.status = "failed";
                    info.error = string.IsNullOrWhiteSpace(payload.error) ? "failed" : payload.error;
                    info.percent = 0;
                }
                RaiseListChanged();
                return;
            }

            // Success metadata means CEF finished and chunks are (or will be) on the wire.
            lock (sync)
            {
                DownloadInfo info;
                if (items.TryGetValue(payload.id, out info) && info.status != "completed")
                {
                    info.status = "transferring";
                    info.percent = Math.Max(info.percent, 100);
                    if (payload.totalBytes > 0)
                        info.size = payload.totalBytes;
                }
            }
            RaiseListChanged();
        }

        public async Task HandleFilePacketAsync(byte[] buffer, int count)
        {
            string id;
            uint seq;
            bool isLast;
            int dataOffset;
            int dataLen;
            if (!TryParseFilePacket(buffer, count, out id, out seq, out isLast, out dataOffset, out dataLen))
                return;

            if (!incoming.ContainsKey(id))
                await WaitForDownloadMetadataAsync(id);

            IncomingTransfer transfer;
            lock (sync)
            {
                if (!incoming.TryGetValue(id, out transfer))
                {
                    DownloadInfo info;
                    items.TryGetValue(id, out info);
                    var fileName = info != null && !string.IsNullOrWhiteSpace(info.fileName)
                        ? info.fileName
                        : ("download_" + id);

                    transfer = new IncomingTransfer
                    {
                        Id = id,
                        FileName = EnsureDownloadFileName(fileName, info?.mimeType),
                        ExpectedSeq = 0
                    };
                    incoming[id] = transfer;

                    if (info != null)
                    {
                        info.status = "transferring";
                        info.fileName = transfer.FileName;
                    }
                }
            }

            try
            {
                if (transfer.Stream == null)
                {
                    // Write straight into Downloads — avoids needing 2× size free for a staging copy (critical for multi‑GB).
                    var destination = await DownloadsFolder.CreateFileAsync(
                        transfer.FileName,
                        CreationCollisionOption.GenerateUniqueName);
                    transfer.DestinationFile = destination;
                    transfer.FileName = destination.Name;
                    transfer.Stream = await destination.OpenAsync(FileAccessMode.ReadWrite);
                    transfer.Output = transfer.Stream.GetOutputStreamAt(0);
                    transfer.Writer = new DataWriter(transfer.Output);
                    transfer.UnflushedBytes = 0;
                }

                if (seq < transfer.ExpectedSeq)
                {
                    // Duplicate / late chunk — re-ACK so a lost ACK cannot stall multi‑GB transfers.
                    try { OnChunkWritten?.Invoke(id, (int)seq); } catch { }
                    return;
                }

                if (seq > transfer.ExpectedSeq)
                {
                    await FailTransferAsync(transfer, "transfer interrupted");
                    return;
                }

                if (dataLen > 0)
                {
                    var chunk = new byte[dataLen];
                    System.Buffer.BlockCopy(buffer, dataOffset, chunk, 0, dataLen);
                    transfer.Writer.WriteBytes(chunk);
                    transfer.BytesWritten += dataLen;
                    transfer.UnflushedBytes += dataLen;

                    // Flush in batches — StoreAsync per 32KB chunk is far too slow for multi‑GB files.
                    const long flushEvery = 512 * 1024;
                    if (transfer.UnflushedBytes >= flushEvery || isLast)
                    {
                        await transfer.Writer.StoreAsync();
                        transfer.UnflushedBytes = 0;
                    }
                }

                transfer.ExpectedSeq++;

                // ACK every chunk so the server window can advance on slow mobile links.
                try { OnChunkWritten?.Invoke(id, (int)seq); } catch { }

                if (!isLast)
                {
                    var shouldNotify = false;
                    lock (sync)
                    {
                        DownloadInfo info;
                        if (items.TryGetValue(id, out info) && info.size > 0)
                        {
                            var percent = (int)Math.Min(100, (transfer.BytesWritten * 100) / info.size);
                            // For multi‑GB, update UI about every 1% to keep UI light.
                            var step = info.size >= (100L * 1024 * 1024) ? 1 : 2;
                            if (percent != info.percent && (percent >= info.percent + step || percent >= 100))
                            {
                                info.percent = percent;
                                info.status = "transferring";
                                info.fileName = transfer.FileName;
                                shouldNotify = true;
                            }
                            else
                            {
                                info.status = "transferring";
                            }
                        }
                    }
                    if (shouldNotify)
                        RaiseListChanged();
                    return;
                }

                await FinalizeTransferAsync(transfer);
            }
            catch (Exception ex)
            {
                await FailTransferAsync(transfer, ex.Message);
            }
        }

        public async Task DeleteAsync(string id)
        {
            string token = null;
            lock (sync)
            {
                DownloadInfo info;
                if (items.TryGetValue(id, out info))
                {
                    token = info.accessToken;
                    items.Remove(id);
                }
                incoming.Remove(id);
            }

            try
            {
                if (!string.IsNullOrEmpty(token) && StorageApplicationPermissions.FutureAccessList.ContainsItem(token))
                {
                    var file = await StorageApplicationPermissions.FutureAccessList.GetFileAsync(token);
                    StorageApplicationPermissions.FutureAccessList.Remove(token);
                    await file.DeleteAsync();
                }
            }
            catch
            {
            }

            await SaveIndexAsync();
            RaiseListChanged();
        }

        public async Task<StorageFile> TryGetFileAsync(string id)
        {
            string token;
            lock (sync)
            {
                DownloadInfo info;
                if (!items.TryGetValue(id, out info) || string.IsNullOrEmpty(info.accessToken))
                    return null;
                token = info.accessToken;
            }

            try
            {
                if (!StorageApplicationPermissions.FutureAccessList.ContainsItem(token))
                    return null;
                return await StorageApplicationPermissions.FutureAccessList.GetFileAsync(token);
            }
            catch
            {
                return null;
            }
        }

        private async Task FinalizeTransferAsync(IncomingTransfer transfer)
        {
            try
            {
                if (transfer.Writer != null)
                {
                    await transfer.Writer.StoreAsync();
                    await transfer.Writer.FlushAsync();
                    transfer.Writer.DetachStream();
                    transfer.Writer.Dispose();
                    transfer.Writer = null;
                }
                if (transfer.Output != null)
                {
                    await transfer.Output.FlushAsync();
                    transfer.Output.Dispose();
                    transfer.Output = null;
                }
                if (transfer.Stream != null)
                {
                    transfer.Stream.Dispose();
                    transfer.Stream = null;
                }

                var downloaded = transfer.DestinationFile;
                if (downloaded == null)
                    throw new InvalidOperationException("missing destination file");

                var token = StorageApplicationPermissions.FutureAccessList.Add(downloaded);
                long size = transfer.BytesWritten;
                try
                {
                    size = (long)(await downloaded.GetBasicPropertiesAsync()).Size;
                }
                catch
                {
                }

                lock (sync)
                {
                    incoming.Remove(transfer.Id);
                    DownloadInfo info;
                    if (!items.TryGetValue(transfer.Id, out info))
                    {
                        info = new DownloadInfo { id = transfer.Id };
                        items[transfer.Id] = info;
                    }

                    info.fileName = downloaded.Name;
                    info.size = size;
                    info.status = "completed";
                    info.percent = 100;
                    info.accessToken = token;
                    info.completedUtc = DateTime.UtcNow.ToString("o");
                    info.error = null;
                }

                await SaveIndexAsync();
                RaiseListChanged();
            }
            catch (Exception ex)
            {
                await FailTransferAsync(transfer, ex.Message);
            }
        }

        private async Task FailTransferAsync(IncomingTransfer transfer, string error)
        {
            try
            {
                transfer.Writer?.DetachStream();
                transfer.Writer?.Dispose();
                transfer.Output?.Dispose();
                transfer.Stream?.Dispose();
                if (transfer.DestinationFile != null)
                {
                    // Incomplete Downloads entry — remove so we do not leave truncated multi‑GB junk.
                    try { await transfer.DestinationFile.DeleteAsync(); } catch { }
                }
            }
            catch
            {
            }

            lock (sync)
            {
                incoming.Remove(transfer.Id);
                DownloadInfo info;
                if (!items.TryGetValue(transfer.Id, out info))
                {
                    info = new DownloadInfo { id = transfer.Id, fileName = transfer.FileName };
                    items[transfer.Id] = info;
                }
                info.status = "failed";
                info.error = error ?? "failed";
            }

            await SaveIndexAsync();
            RaiseListChanged();
        }

        private async Task SaveIndexAsync()
        {
            List<DownloadInfo> snapshot;
            lock (sync)
            {
                snapshot = items.Values.Select(Clone).ToList();
            }

            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    IndexFileName,
                    CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, JsonConvert.SerializeObject(snapshot));
            }
            catch
            {
            }
        }

        private void RaiseListChanged()
        {
            ListChanged?.Invoke(this, EventArgs.Empty);
        }

        private static DownloadInfo Clone(DownloadInfo src)
        {
            return new DownloadInfo
            {
                id = src.id,
                fileName = src.fileName,
                mimeType = src.mimeType,
                size = src.size,
                status = src.status,
                percent = src.percent,
                accessToken = src.accessToken,
                completedUtc = src.completedUtc,
                error = src.error
            };
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "download";

            name = Path.GetFileName(name.Trim());
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return "download";
            if (name.Length > 120)
                name = name.Substring(0, 120);
            return name;
        }

        private async Task WaitForDownloadMetadataAsync(string id)
        {
            var deadline = Environment.TickCount + 2000;
            while (Environment.TickCount < deadline)
            {
                lock (sync)
                {
                    if (items.ContainsKey(id))
                        return;
                }
                await Task.Delay(25);
            }
        }

        private static string EnsureDownloadFileName(string fileName, string mimeType)
        {
            fileName = SanitizeFileName(fileName);
            var ext = Path.GetExtension(fileName);
            if (!string.IsNullOrEmpty(ext) && ext.Length > 1)
                return fileName;

            ext = ExtensionFromMimeType(mimeType) ?? ".jpg";
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(baseName) || baseName.StartsWith("download", StringComparison.OrdinalIgnoreCase))
                baseName = "image";
            return baseName + ext;
        }

        private static string ExtensionFromMimeType(string mimeType)
        {
            if (string.IsNullOrWhiteSpace(mimeType))
                return null;

            switch (mimeType.Split(';')[0].Trim().ToLowerInvariant())
            {
                case "image/jpeg":
                case "image/jpg":
                    return ".jpg";
                case "image/png":
                    return ".png";
                case "image/gif":
                    return ".gif";
                case "image/webp":
                    return ".webp";
                case "image/bmp":
                    return ".bmp";
                case "image/svg+xml":
                    return ".svg";
                default:
                    return null;
            }
        }

        private static bool TryParseFilePacket(
            byte[] buffer,
            int count,
            out string id,
            out uint seq,
            out bool isLast,
            out int dataOffset,
            out int dataLen)
        {
            id = null;
            seq = 0;
            isLast = false;
            dataOffset = 0;
            dataLen = 0;

            if (buffer == null || count < 4 + 2 + 4 + 1 + 4)
                return false;
            if (buffer[0] != (byte)'F' || buffer[1] != (byte)'I' || buffer[2] != (byte)'L' || buffer[3] != (byte)'E')
                return false;

            int o = 4;
            int idLen = buffer[o] | (buffer[o + 1] << 8);
            o += 2;
            if (idLen < 1 || idLen > 64 || o + idLen + 4 + 1 + 4 > count)
                return false;

            id = System.Text.Encoding.UTF8.GetString(buffer, o, idLen);
            o += idLen;
            seq = ReadUInt32(buffer, o); o += 4;
            isLast = (buffer[o] & FlagLast) != 0; o += 1;
            dataLen = (int)ReadUInt32(buffer, o); o += 4;
            if (dataLen < 0 || o + dataLen > count)
                return false;

            dataOffset = o;
            return true;
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }

        private sealed class IncomingTransfer
        {
            public string Id;
            public string FileName;
            public uint ExpectedSeq;
            public long BytesWritten;
            public long UnflushedBytes;
            public StorageFile DestinationFile;
            public IRandomAccessStream Stream;
            public IOutputStream Output;
            public DataWriter Writer;
        }
    }
}
