using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace ATTM.Plugin.MidiAi;

/// <summary>下载进度。</summary>
public sealed record DownloadProgress(long Received, long Total, int ActiveChunks)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Received * 100.0 / Total, 0, 100);
}

/// <summary>
/// 多线程分块下载：先探一次长度与 Range 支持，再按并发数切片，
/// 每片一个 HTTP Range 请求并发写进同一个文件的不同偏移。
/// 服务器不支持 Range 时自动退回单连接下载。
/// </summary>
public static class ModelDownloader
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task DownloadAsync(
        string url,
        string targetPath,
        int concurrency,
        IProgress<DownloadProgress>? progress,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("下载地址为空", nameof(url));

        concurrency = Math.Clamp(concurrency, 1, 16);

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var partPath = targetPath + ".part";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        using var probe = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

        long total = 0;
        var supportsRange = probe.StatusCode == HttpStatusCode.PartialContent;

        if (supportsRange)
        {
            total = probe.Content.Headers.ContentRange?.Length ?? 0;
        }
        else if (probe.IsSuccessStatusCode)
        {
            total = probe.Content.Headers.ContentLength ?? 0;
        }
        else
        {
            throw new InvalidOperationException("服务器返回 " + (int)probe.StatusCode + "，无法下载：" + url);
        }

        if (total <= 0)
            throw new InvalidOperationException("拿不到文件长度（服务器没给 Content-Length）");

        if (!supportsRange || concurrency == 1 || total < 1024 * 1024)
        {
            await DownloadSingleAsync(url, partPath, total, progress, token).ConfigureAwait(false);
        }
        else
        {
            await DownloadChunkedAsync(url, partPath, total, concurrency, progress, token).ConfigureAwait(false);
        }

        if (File.Exists(targetPath)) File.Delete(targetPath);
        File.Move(partPath, targetPath);
    }

    private static async Task DownloadSingleAsync(
        string url, string path, long total, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long received = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read <= 0) break;

            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            received += read;
            progress?.Report(new DownloadProgress(received, total, 1));
        }
    }

    private static async Task DownloadChunkedAsync(
        string url, string path, long total, int concurrency, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        await using (var allocate = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            allocate.SetLength(total);
        }

        var chunkSize = total / concurrency;
        var received = 0L;
        var active = concurrency;

        var tasks = new List<Task>(concurrency);

        for (var i = 0; i < concurrency; i++)
        {
            var start = i * chunkSize;
            var end = i == concurrency - 1 ? total - 1 : start + chunkSize - 1;

            tasks.Add(Task.Run(async () =>
            {
                await DownloadChunkAsync(url, path, start, end, onRead: n =>
                {
                    var done = Interlocked.Add(ref received, n);
                    progress?.Report(new DownloadProgress(done, total, Volatile.Read(ref active)));
                }, token).ConfigureAwait(false);
            }, token));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task DownloadChunkAsync(
        string url, string path, long start, long end, Action<int> onRead, CancellationToken token)
    {
        Exception? last = null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(start, end);

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var target = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 81920, true);

                target.Seek(start, SeekOrigin.Begin);

                var buffer = new byte[81920];
                long written = 0;

                while (written < end - start + 1)
                {
                    var want = (int)Math.Min(buffer.Length, end - start + 1 - written);
                    var read = await source.ReadAsync(buffer.AsMemory(0, want), token).ConfigureAwait(false);
                    if (read <= 0) break;

                    await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    written += read;
                    onRead(read);
                }

                return;
            }
            catch (Exception ex) when (attempt < 2 && !token.IsCancellationRequested)
            {
                last = ex;
                await Task.Delay(500 * (attempt + 1), token).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("分块下载失败（" + start + "-" + end + "）：" + last?.Message, last);
    }
}
