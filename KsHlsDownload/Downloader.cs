using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace KsHlsDownload
{
    public class Downloader
    {
        private const int BufferSize = 65536;
        private const int MaxTimeoutMinutes = 3;
        private const int MaxTimeoutSeconds = MaxTimeoutMinutes * 60;
        private string _downloadPath;
        private readonly string _tempPath;
        private string _cookie = string.Empty;

        public string Cookie
        {
            get => _cookie;
            set => _cookie = value;
        }

        public string DownloadPath
        {
            get => _downloadPath;
            set
            {
                _downloadPath = value;
            }
        }

        public event EventHandler<DownloadProgressEventArgs> ProgressChanged;
        public event EventHandler<DownloadCompleteEventArgs> DownloadComplete;

        public Downloader(string downloadPath)
        {
            _downloadPath = downloadPath;
            _tempPath = "Temp";// Path.Combine("Temp");

            // 设置 TLS 协议支持
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;

            // 忽略证书验证错误（开发环境）
            ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;

            EnsureDirectoryExists(downloadPath);
            EnsureDirectoryExists(_tempPath);
        }

        public Downloader(string downloadPath, string cookie)
            : this(downloadPath)
        {
            _cookie = cookie;
        }

        private void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        public async Task DownloadFileAsync(string url, string fileName)
        {
            await DownloadFileAsync(url, fileName, null);
        }

        public async Task DownloadFileAsync(string url, string fileName, string cookie)
        {
            string filePath = Path.Combine(_downloadPath, fileName);
            string tempFilePath = Path.Combine(_tempPath, fileName);
            string currentCookie = !string.IsNullOrEmpty(cookie) ? cookie : _cookie;

            try
            {
                await DownloadWithResumeAsync(url, tempFilePath, filePath, fileName, currentCookie);
            }
            catch (Exception ex)
            {
                DownloadComplete?.Invoke(this, new DownloadCompleteEventArgs
                {
                    FileName = fileName,
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        private async Task DownloadWithResumeAsync(string url, string tempFilePath, string finalFilePath, string fileName, string cookie)
        {
            long startPosition = 0;
            long totalBytes = 0;

            if (File.Exists(tempFilePath))
            {
                startPosition = new FileInfo(tempFilePath).Length;
            }

            HttpWebRequest request = null;
            HttpWebResponse response = null;
            Stream responseStream = null;
            FileStream fileStream = null;

            try
            {
                request = CreateRequest(url, cookie);

                if (startPosition > 0)
                {
                    request.AddRange((long)startPosition);
                }

                response = (HttpWebResponse)await request.GetResponseAsync();

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    File.Delete(tempFilePath);
                    startPosition = 0;
                    request = CreateRequest(url, cookie);
                    response = (HttpWebResponse)await request.GetResponseAsync();
                }

                totalBytes = response.ContentLength + startPosition;

                responseStream = response.GetResponseStream();
                fileStream = new FileStream(tempFilePath, FileMode.Append, FileAccess.Write);

                byte[] buffer = new byte[BufferSize];
                int bytesRead;
                long totalBytesRead = startPosition;
                long lastProgressUpdate = 0;

                while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;

                    long currentTime = DateTime.Now.Ticks;
                    if (currentTime - lastProgressUpdate > TimeSpan.TicksPerSecond)
                    {
                        int progress = totalBytes > 0 ? (int)((totalBytesRead * 100) / totalBytes) : 0;

                        ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                        {
                            FileName = fileName,
                            Progress = progress,
                            BytesReceived = totalBytesRead,
                            TotalBytesToReceive = totalBytes
                        });
                        lastProgressUpdate = currentTime;
                    }
                }

                fileStream.Flush();
                fileStream.Close();
                responseStream.Close();

                ValidateDownloadedFile(tempFilePath, totalBytes);

                if (File.Exists(finalFilePath))
                {
                    File.Delete(finalFilePath);
                }

                File.Move(tempFilePath, finalFilePath);

                DownloadComplete?.Invoke(this, new DownloadCompleteEventArgs
                {
                    FileName = fileName,
                    FilePath = finalFilePath,
                    Success = true
                });
            }
            catch (WebException webEx)
            {
                if (webEx.Response != null)
                {
                    using (var errorResponse = (HttpWebResponse)webEx.Response)
                    {
                        throw new Exception($"HTTP {errorResponse.StatusCode}: {errorResponse.StatusDescription}");
                    }
                }
                throw new Exception($"下载失败: {webEx.Message}");
            }
            finally
            {
                fileStream?.Dispose();
                responseStream?.Dispose();
                response?.Dispose();
                request?.Abort();
            }
        }

        private HttpWebRequest CreateRequest(string url, string cookie)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
            request.Timeout = MaxTimeoutSeconds * 1000;
            request.ReadWriteTimeout = MaxTimeoutSeconds * 1000;
            request.AllowAutoRedirect = true;
            request.MaximumAutomaticRedirections = 5;

            if (!string.IsNullOrEmpty(cookie))
            {
                request.Headers["Cookie"] = cookie;
            }

            return request;
        }

        private void ValidateDownloadedFile(string filePath, long expectedSize)
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length != expectedSize)
            {
                throw new Exception($"文件下载不完整。预期大小: {expectedSize} 字节，实际大小: {fileInfo.Length} 字节");
            }
        }

        public async Task DownloadFilesAsync(List<DownloadItem> items)
        {
            foreach (var item in items)
            {
                await DownloadFileAsync(item.Url, item.FileName);
            }
        }

        public string GetDownloadPath()
        {
            return _downloadPath;
        }
    }

    public class DownloadItem
    {
        public string Url { get; set; }
        public string FileName { get; set; }
        public string DetailId { get; set; }
        public string PhotoType { get; set; }
        public string Cookie { get; set; }
    }

    public class DownloadProgressEventArgs : EventArgs
    {
        public string FileName { get; set; }
        public int Progress { get; set; }
        public long BytesReceived { get; set; }
        public long TotalBytesToReceive { get; set; }
    }

    public class DownloadCompleteEventArgs : EventArgs
    {
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
    }
}
