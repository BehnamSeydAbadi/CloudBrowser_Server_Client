using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using CefSharp;
using CefSharp.Handler;
using Newtonsoft.Json;

namespace BrowserServer
{
    /// <summary>
    /// Captures CEF downloads to a temp folder, then streams them to the phone as FILE chunks.
    /// Uses a small ACK window so the phone WebSocket is not flooded (large files previously aborted the socket).
    /// Binary layout:
    ///   magic[4]="FILE" | idLen:u16 | id utf8 | seq:u32 | flags:u8 | dataLen:u32 | data
    /// flags bit0 = last chunk
    /// </summary>
    public class StreamingDownloadHandler : DownloadHandler
    {
        public static readonly byte[] Magic = { (byte)'F', (byte)'I', (byte)'L', (byte)'E' };
        private const int DefaultChunkSize = 32 * 1024;
        private const int LargeChunkSize = 64 * 1024;
        private const long LargeFileThresholdBytes = 100L * 1024 * 1024; // 100 MB+
        private const int MaxChunksPerFlush = 2;
        private const int DefaultAckWindow = 6;
        private const int LargeAckWindow = 8;
        private const int DefaultAckTimeoutMs = 45000;
        private const int LargeAckTimeoutMs = 180000; // multi‑GB transfers need long idle tolerance
        private const byte FlagLast = 1;

        private static readonly ConcurrentQueue<PendingStream> Pending = new ConcurrentQueue<PendingStream>();
        private static readonly ConcurrentDictionary<int, ActiveDownload> Active = new ConcurrentDictionary<int, ActiveDownload>();
        private static readonly object FlushLock = new object();
        private static PendingStream current;
        private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "CloudBrowserDownloads");

        private readonly string tabId;

        public StreamingDownloadHandler(string tabId)
        {
            this.tabId = tabId;
            Directory.CreateDirectory(TempRoot);
        }

        /// <summary>Remove leftover temp downloads from previous runs / crashed streams.</summary>
        public static void PurgeTempFolder()
        {
            try
            {
                Directory.CreateDirectory(TempRoot);
                var files = Directory.GetFiles(TempRoot);
                var removed = 0;
                foreach (var file in files)
                {
                    if (TryDelete(file, retries: 2))
                        removed++;
                }
                if (removed > 0)
                    Console.WriteLine("Download temp cleanup: removed {0} leftover file(s) from {1}", removed, TempRoot);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Download temp cleanup error: " + ex.Message);
            }
        }

        public static bool IsStreamingToClients
        {
            get { return current != null || !Pending.IsEmpty; }
        }

        public static void HandleClientAck(string id, int seq)
        {
            if (string.IsNullOrEmpty(id) || seq < 0)
                return;

            lock (FlushLock)
            {
                if (current == null || !string.Equals(current.Id, id, StringComparison.Ordinal))
                    return;

                if (seq > current.LastAckedSeq)
                {
                    current.LastAckedSeq = seq;
                    current.LastAckTick = Environment.TickCount;
                }
            }
        }

        public static void FlushOutbound()
        {
            if (!Monitor.TryEnter(FlushLock))
                return;

            try
            {
                var server = TabManager.Server;
                if (server == null)
                    return;

                int sent = 0;
                while (sent < MaxChunksPerFlush)
                {
                    if (current == null)
                    {
                        if (!Pending.TryDequeue(out current))
                            return;

                        try
                        {
                            if (!File.Exists(current.Path))
                                throw new FileNotFoundException("Download temp missing", current.Path);

                            current.Stream = new FileStream(
                                current.Path,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read,
                                current.ChunkSize,
                                FileOptions.SequentialScan);
                            current.Buffer = new byte[current.ChunkSize];
                            current.Seq = 0;
                            current.LastAckedSeq = -1;
                            current.TotalBytes = current.Stream.Length;
                            current.LastAckTick = Environment.TickCount;
                            current.WaitingForFinalAck = false;
                            Console.WriteLine(
                                "Download stream begin id={0} bytes={1} chunk={2}KB window={3} queued={4}",
                                current.Id,
                                current.TotalBytes,
                                current.ChunkSize / 1024,
                                current.AckWindow,
                                Pending.Count);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Download stream open error: " + ex.Message);
                            FailCurrent(ex.Message);
                            continue;
                        }
                    }

                    // Finished sending; wait for the phone to ACK the last chunk.
                    if (current.WaitingForFinalAck)
                    {
                        if (current.LastAckedSeq >= current.FinalSeq)
                        {
                            FinishCurrentSuccess();
                            current = null;
                            continue;
                        }

                        if (Environment.TickCount - current.LastAckTick > current.AckTimeoutMs)
                        {
                            Console.WriteLine("Download final ACK timeout id={0}", current.Id);
                            FailCurrent("transfer interrupted");
                        }
                        return;
                    }

                    // Sliding window — do not outrun the phone.
                    if (current.Seq - current.LastAckedSeq > current.AckWindow)
                    {
                        if (Environment.TickCount - current.LastAckTick > current.AckTimeoutMs)
                        {
                            Console.WriteLine("Download ACK timeout id={0} seq={1} acked={2}",
                                current.Id, current.Seq, current.LastAckedSeq);
                            FailCurrent("transfer interrupted");
                        }
                        return;
                    }

                    if (CountConnectedClients(server) <= 0)
                    {
                        Console.WriteLine("Download aborted — no connected clients id={0}", current.Id);
                        FailCurrent("client disconnected");
                        return;
                    }

                    try
                    {
                        int read;
                        bool isLast;

                        if (current.TotalBytes == 0)
                        {
                            read = 0;
                            isLast = true;
                        }
                        else
                        {
                            read = current.Stream.Read(current.Buffer, 0, current.Buffer.Length);
                            if (read < 0)
                                read = 0;
                            isLast = current.Stream.Position >= current.Stream.Length;
                            if (read == 0 && !isLast)
                                throw new IOException("Unexpected EOF while streaming download");
                        }

                        var packet = BuildChunkPacket(current.Id, current.Seq, isLast, current.Buffer, read);
                        if (!TrySendBinaryToClients(server, packet))
                        {
                            Console.WriteLine("Download send failed id={0} seq={1}", current.Id, current.Seq);
                            FailCurrent("client disconnected");
                            return;
                        }

                        var sentSeq = current.Seq;
                        current.Seq++;
                        current.BytesSent += read;
                        current.LastAckTick = Environment.TickCount;
                        sent++;

                        if (current.TotalBytes > 0 && (isLast || sentSeq % 8 == 0))
                        {
                            var percent = (int)Math.Min(100, (current.BytesSent * 100) / current.TotalBytes);
                            BroadcastText(TextPacketType.DownloadProgress, new DownloadEventPayload
                            {
                                id = current.Id,
                                fileName = current.FileName,
                                totalBytes = current.TotalBytes,
                                receivedBytes = current.BytesSent,
                                percent = percent,
                                mimeType = current.MimeType,
                                success = true
                            });
                        }

                        if (isLast)
                        {
                            current.WaitingForFinalAck = true;
                            current.FinalSeq = sentSeq;
                            // Next ticks wait for ACK before FinishCurrentSuccess.
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Download stream send error id={0}: {1}", current?.Id, ex.Message);
                        FailCurrent("transfer interrupted");
                        return;
                    }
                }
            }
            finally
            {
                Monitor.Exit(FlushLock);
            }
        }

        private static int CountConnectedClients(WebSocketSharp.Server.WebSocketServer server)
        {
            try
            {
                var host = server.WebSocketServices["/"];
                return host != null ? host.Sessions.Count : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Send to each open session explicitly. Returns false if nobody received the packet.
        /// WebSocketSharp Broadcast swallows disconnects, which made 40MB transfers look "finished".
        /// </summary>
        private static bool TrySendBinaryToClients(WebSocketSharp.Server.WebSocketServer server, byte[] packet)
        {
            try
            {
                var host = server.WebSocketServices["/"];
                if (host == null || host.Sessions.Count == 0)
                    return false;

                var ids = host.Sessions.ActiveIDs.ToList();
                if (ids.Count == 0)
                    ids = host.Sessions.IDs.ToList();
                int ok = 0;
                foreach (var id in ids)
                {
                    try
                    {
                        host.Sessions.SendTo(packet, id);
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Download SendTo failed session={0}: {1}", id, ex.Message);
                    }
                }

                return ok > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Download send error: " + ex.Message);
                return false;
            }
        }

        private static void FinishCurrentSuccess()
        {
            if (current == null)
                return;

            var done = current;
            current = null;

            try { done.Stream?.Dispose(); } catch { }
            done.Stream = null;

            BroadcastText(TextPacketType.DownloadCompleted, new DownloadEventPayload
            {
                id = done.Id,
                fileName = done.FileName,
                totalBytes = done.TotalBytes,
                receivedBytes = done.BytesSent,
                percent = 100,
                mimeType = done.MimeType,
                success = true
            });

            Console.WriteLine("Download stream finished id={0} bytes={1}", done.Id, done.BytesSent);
            if (TryDelete(done.Path, retries: 5))
                Console.WriteLine("Download temp deleted: {0}", done.Path);
            else
                Console.WriteLine("Download temp delete FAILED (will retry on next purge): {0}", done.Path);
        }

        private static void FailCurrent(string error)
        {
            if (current == null)
                return;

            var failed = current;
            current = null;

            try { failed.Stream?.Dispose(); } catch { }
            failed.Stream = null;

            BroadcastText(TextPacketType.DownloadCompleted, new DownloadEventPayload
            {
                id = failed.Id,
                fileName = failed.FileName,
                totalBytes = failed.TotalBytes,
                receivedBytes = failed.BytesSent,
                percent = 0,
                mimeType = failed.MimeType,
                success = false,
                error = error ?? "failed"
            });

            if (TryDelete(failed.Path, retries: 5))
                Console.WriteLine("Download temp deleted after failure: {0}", failed.Path);
            else
                Console.WriteLine("Download temp delete FAILED after failure: {0}", failed.Path);
        }

        protected override bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod)
        {
            return true;
        }

        protected override void OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem, IBeforeDownloadCallback callback)
        {
            if (callback.IsDisposed)
                return;

            var fileName = SanitizeFileName(downloadItem.SuggestedFileName);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "download";

            var id = Guid.NewGuid().ToString("N");
            var tempPath = Path.Combine(TempRoot, id + "_" + fileName);

            var active = new ActiveDownload
            {
                Id = id,
                CefId = downloadItem.Id,
                FileName = fileName,
                TempPath = tempPath,
                MimeType = downloadItem.MimeType ?? "",
                TotalBytes = downloadItem.TotalBytes
            };
            Active[downloadItem.Id] = active;

            Console.WriteLine("Download start tab={0} id={1} file={2}", tabId, id, fileName);
            BroadcastText(TextPacketType.DownloadStarted, new DownloadEventPayload
            {
                id = id,
                fileName = fileName,
                totalBytes = downloadItem.TotalBytes,
                receivedBytes = 0,
                percent = 0,
                mimeType = active.MimeType,
                success = true
            });

            using (callback)
            {
                callback.Continue(tempPath, showDialog: false);
            }
        }

        protected override void OnDownloadUpdated(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem, IDownloadItemCallback callback)
        {
            ActiveDownload active;
            if (!Active.TryGetValue(downloadItem.Id, out active))
                return;

            if (downloadItem.IsInProgress)
            {
                active.TotalBytes = downloadItem.TotalBytes > 0 ? downloadItem.TotalBytes : active.TotalBytes;
                active.ReceivedBytes = downloadItem.ReceivedBytes;
                var percent = downloadItem.PercentComplete;
                if (percent < 0) percent = 0;

                if (percent != active.LastNotifiedPercent && (percent == 0 || percent >= active.LastNotifiedPercent + 5 || percent >= 100))
                {
                    active.LastNotifiedPercent = percent;
                    var reportPercent = Math.Min(percent, 95);
                    BroadcastText(TextPacketType.DownloadProgress, new DownloadEventPayload
                    {
                        id = active.Id,
                        fileName = active.FileName,
                        totalBytes = active.TotalBytes,
                        receivedBytes = downloadItem.ReceivedBytes,
                        percent = reportPercent,
                        mimeType = active.MimeType,
                        success = true
                    });
                }
                return;
            }

            if (!Active.TryRemove(downloadItem.Id, out active) || active == null)
                return;

                if (downloadItem.IsCancelled)
                {
                    if (TryDelete(active.TempPath, retries: 5))
                        Console.WriteLine("Download temp deleted (cancelled): {0}", active.TempPath);
                    BroadcastText(TextPacketType.DownloadCompleted, new DownloadEventPayload
                    {
                        id = active.Id,
                        fileName = active.FileName,
                        totalBytes = active.TotalBytes,
                        receivedBytes = downloadItem.ReceivedBytes,
                        percent = 0,
                        mimeType = active.MimeType,
                        success = false,
                        error = "cancelled"
                    });
                    return;
                }

            if (!downloadItem.IsComplete)
                return;

            var path = !string.IsNullOrEmpty(downloadItem.FullPath) ? downloadItem.FullPath : active.TempPath;
            try
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException("Download temp file missing", path);

                var length = new FileInfo(path).Length;
                active.TotalBytes = length;
                Console.WriteLine("Download complete id={0} bytes={1} — queued for phone stream (pending={2})",
                    active.Id, length, Pending.Count + 1);

                BroadcastText(TextPacketType.DownloadProgress, new DownloadEventPayload
                {
                    id = active.Id,
                    fileName = active.FileName,
                    totalBytes = length,
                    receivedBytes = length,
                    percent = 95,
                    mimeType = active.MimeType,
                    success = true
                });

                var large = length >= LargeFileThresholdBytes;
                Pending.Enqueue(new PendingStream
                {
                    Id = active.Id,
                    Path = path,
                    FileName = active.FileName,
                    MimeType = active.MimeType,
                    TotalBytes = length,
                    ChunkSize = large ? LargeChunkSize : DefaultChunkSize,
                    AckWindow = large ? LargeAckWindow : DefaultAckWindow,
                    AckTimeoutMs = large ? LargeAckTimeoutMs : DefaultAckTimeoutMs
                });

                if (large)
                    Console.WriteLine("Large download queued ({0:0.##} GB) — using {1}KB chunks, {2}s ACK timeout",
                        length / (1024.0 * 1024.0 * 1024.0),
                        large ? LargeChunkSize / 1024 : DefaultChunkSize / 1024,
                        (large ? LargeAckTimeoutMs : DefaultAckTimeoutMs) / 1000);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Download queue error: " + ex.Message);
                BroadcastText(TextPacketType.DownloadCompleted, new DownloadEventPayload
                {
                    id = active.Id,
                    fileName = active.FileName,
                    totalBytes = active.TotalBytes,
                    receivedBytes = downloadItem.ReceivedBytes,
                    percent = 0,
                    mimeType = active.MimeType,
                    success = false,
                    error = ex.Message
                });
                TryDelete(path, retries: 5);
                if (!string.Equals(path, active.TempPath, StringComparison.OrdinalIgnoreCase))
                    TryDelete(active.TempPath, retries: 5);
            }
        }

        private static byte[] BuildChunkPacket(string downloadId, int seq, bool isLast, byte[] data, int dataLen)
        {
            var idBytes = Encoding.UTF8.GetBytes(downloadId ?? "");
            if (idBytes.Length > 64)
            {
                var trimmed = new byte[64];
                Buffer.BlockCopy(idBytes, 0, trimmed, 0, 64);
                idBytes = trimmed;
            }

            var packet = new byte[4 + 2 + idBytes.Length + 4 + 1 + 4 + dataLen];
            var o = 0;
            Buffer.BlockCopy(Magic, 0, packet, o, 4); o += 4;
            packet[o++] = (byte)(idBytes.Length & 0xFF);
            packet[o++] = (byte)((idBytes.Length >> 8) & 0xFF);
            Buffer.BlockCopy(idBytes, 0, packet, o, idBytes.Length); o += idBytes.Length;
            WriteUInt32(packet, o, (uint)seq); o += 4;
            packet[o++] = isLast ? FlagLast : (byte)0;
            WriteUInt32(packet, o, (uint)dataLen); o += 4;
            if (dataLen > 0)
                Buffer.BlockCopy(data, 0, packet, o, dataLen);
            return packet;
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void BroadcastText(TextPacketType type, DownloadEventPayload payload)
        {
            try
            {
                TabManager.Server?.WebSocketServices.Broadcast(JsonConvert.SerializeObject(new TextPacket
                {
                    PType = type,
                    text = JsonConvert.SerializeObject(payload)
                }));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Download text broadcast error: " + ex.Message);
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "download";

            name = Path.GetFileName(name.Trim());
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            if (string.IsNullOrWhiteSpace(name))
                return "download";
            if (name.Length > 120)
                name = name.Substring(0, 120);
            return name;
        }

        private static bool TryDelete(string path, int retries = 3)
        {
            if (string.IsNullOrEmpty(path))
                return true;

            for (int attempt = 0; attempt < retries; attempt++)
            {
                try
                {
                    if (!File.Exists(path))
                        return true;

                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    if (!File.Exists(path))
                        return true;
                }
                catch
                {
                    // File may still be flushing; brief backoff then retry.
                }

                try { Thread.Sleep(40 * (attempt + 1)); } catch { }
            }

            try
            {
                if (!File.Exists(path))
                    return true;
                File.Delete(path);
                return !File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private sealed class ActiveDownload
        {
            public string Id;
            public int CefId;
            public string FileName;
            public string TempPath;
            public string MimeType;
            public long TotalBytes;
            public long ReceivedBytes;
            public int LastNotifiedPercent = -1;
        }

        private sealed class PendingStream
        {
            public string Id;
            public string Path;
            public string FileName;
            public string MimeType;
            public long TotalBytes;
            public long BytesSent;
            public int Seq;
            public int LastAckedSeq = -1;
            public int LastAckTick;
            public bool WaitingForFinalAck;
            public int FinalSeq;
            public int ChunkSize = DefaultChunkSize;
            public int AckWindow = DefaultAckWindow;
            public int AckTimeoutMs = DefaultAckTimeoutMs;
            public FileStream Stream;
            public byte[] Buffer;
        }
    }
}
