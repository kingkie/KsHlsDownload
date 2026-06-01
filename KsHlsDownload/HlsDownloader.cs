using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KsHlsDownload
{
    public class HlsDownloader
    {
        private const int BufferSize = 65536;
        private const int MaxTimeoutSeconds = 300;
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
        /// <summary>
        /// 文件名
        /// </summary>
        public string FileName
        {
            get;
            set;
        }

        public event EventHandler<DownloadProgressEventArgs> ProgressChanged;
        public event EventHandler<DownloadCompleteEventArgs> DownloadComplete;
        public event EventHandler<string> LogMessage;

        public HlsDownloader(string downloadPath)
        {
            _downloadPath = downloadPath;
            _tempPath = "Temp";// Path.Combine(Path.GetTempPath(), "KsLoader_HLS");

            // 设置 TLS 协议支持
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;

            // 忽略证书验证错误（开发环境）
            ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;

            EnsureDirectoryExists(downloadPath);
            EnsureDirectoryExists(_tempPath);
        }

        private void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        public async Task DownloadAsync(string url, string detailId)
        {
            try
            {
                await DownloadHlsVideoAsync(url, detailId);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"HLS下载失败: {ex.Message}");
                DownloadComplete?.Invoke(this, new DownloadCompleteEventArgs
                {
                    FileName = $"{detailId}_hls.mp4",
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        private async Task DownloadHlsVideoAsync(string url, string detailId)
        {
            LogMessage?.Invoke(this, "HLS下载: 获取页面内容...");

            // 1. 获取页面内容，查找m3u8 URL
            string content = await DownloadStringAsync(url);

            // 调试：保存页面内容
            string debugPath = Path.Combine(_tempPath, $"debug_{detailId}.html");
            File.WriteAllText(debugPath, content);
            LogMessage?.Invoke(this, $"HLS下载: 页面内容已保存到: {debugPath}");

            string m3u8Url = FindM3u8Url(content);

            if (string.IsNullOrEmpty(m3u8Url))
            {
                throw new Exception("HLS下载: 未找到m3u8 URL");
            }

            LogMessage?.Invoke(this, $"HLS下载: 找到m3u8 URL: {m3u8Url.Substring(0, Math.Min(m3u8Url.Length, 100))}...");

            // 2. 获取m3u8文件
            LogMessage?.Invoke(this, "HLS下载: 获取m3u8文件...");
            string m3u8Content = await DownloadStringAsync(m3u8Url);

            // 3. 解析m3u8获取所有ts文件
            LogMessage?.Invoke(this, "HLS下载: 解析m3u8文件...");
            List<string> tsUrls = ParseM3u8(m3u8Content, m3u8Url);

            if (tsUrls.Count == 0)
            {
                throw new Exception("HLS下载: 未找到ts分片");
            }

            LogMessage?.Invoke(this, $"HLS下载: 找到 {tsUrls.Count} 个ts分片");

            // 4. 下载所有ts文件
            LogMessage?.Invoke(this, "HLS下载: 开始下载ts分片...");
            List<string> tsFiles = await DownloadTsFiles(tsUrls, detailId);

            string strFilename = string.Empty;
            if (!string.IsNullOrEmpty(FileName))
            {
                strFilename = FileName;
            }
            else
            {
                strFilename = detailId;
            }

            // 5. 合并ts文件
            LogMessage?.Invoke(this, "HLS下载: 合并ts文件...");
            string outputPath = await MergeTsFiles(tsFiles, detailId, strFilename);

            // 6. 检查文件大小
            long fileSize = new FileInfo(outputPath).Length;
            LogMessage?.Invoke(this, $"HLS下载完成! 文件大小: {FormatFileSize(fileSize)}");
            LogMessage?.Invoke(this, $"HLS视频已保存: {outputPath}");



            DownloadComplete?.Invoke(this, new DownloadCompleteEventArgs
            {
                FileName = $"{strFilename}.mp4",
                FilePath = outputPath,
                Success = true
            });
        }

        private async Task<string> DownloadStringAsync(string url)
        {
            HttpWebRequest request = CreateRequest(url);

            using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync())
            using (Stream responseStream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(responseStream))
            {
                return await reader.ReadToEndAsync();
            }
        }

        private async Task<byte[]> DownloadBytesAsync(string url)
        {
            HttpWebRequest request = CreateRequest(url);

            using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync())
            using (Stream responseStream = response.GetResponseStream())
            {
                MemoryStream ms = new MemoryStream();
                byte[] buffer = new byte[BufferSize];
                int bytesRead;

                while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, bytesRead);
                }

                return ms.ToArray();
            }
        }

        private HttpWebRequest CreateRequest(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Mozilla/5.0 (Linux; Android 10; SM-G970F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/92.0.4515.131 Mobile Safari/537.36";
            request.Referer = "https://v.m.chenzhongtech.com/";
            request.Timeout = MaxTimeoutSeconds * 1000;
            request.ReadWriteTimeout = MaxTimeoutSeconds * 1000;
            request.AllowAutoRedirect = true;
            request.MaximumAutomaticRedirections = 5;

            if (!string.IsNullOrEmpty(_cookie))
            {
                request.Headers["Cookie"] = _cookie;
            }

            return request;
        }

        private string FindM3u8Url(string content)
        {
            LogMessage?.Invoke(this, "HLS下载: 正在查找m3u8 URL...");

            // 模式1: 直接搜索.m3u8 URL
            Match match = Regex.Match(content, @"https?://[^\s,""'<>]+\.m3u8[^\s,""'<>]*");
            if (match.Success)
            {
                LogMessage?.Invoke(this, "HLS下载: 找到m3u8 URL (模式1)");
                return match.Value;
            }

            // 模式2: 搜索video-hls模式
            match = Regex.Match(content, @"https?://[^\s,""'<>]+video-hls/[^\s,""'<>]*\.m3u8");
            if (match.Success)
            {
                LogMessage?.Invoke(this, "HLS下载: 找到m3u8 URL (模式2)");
                return match.Value;
            }

            // 模式3: 搜索playUrl模式
            match = Regex.Match(content, @"playUrl[\s]*:[\s]*""([^""]+\.m3u8[^""]*)""");
            if (match.Success && match.Groups.Count > 1)
            {
                LogMessage?.Invoke(this, "HLS下载: 找到m3u8 URL (模式3-playUrl)");
                return match.Groups[1].Value;
            }

            // 模式4: 搜索src模式
            match = Regex.Match(content, @"src[\s]*:[\s]*""([^""]+\.m3u8[^""]*)""");
            if (match.Success && match.Groups.Count > 1)
            {
                LogMessage?.Invoke(this, "HLS下载: 找到m3u8 URL (模式4-src)");
                return match.Groups[1].Value;
            }

            // 模式5: 搜索所有可能的m3u8链接
            MatchCollection matches = Regex.Matches(content, @"https?://[^""'\s<]+");
            foreach (Match m in matches)
            {
                if (m.Value.Contains("m3u8"))
                {
                    LogMessage?.Invoke(this, $"HLS下载: 找到m3u8 URL (模式5)");
                    return m.Value;
                }
            }

            LogMessage?.Invoke(this, "HLS下载: 未找到任何m3u8 URL");
            return null;
        }

        private List<string> ParseM3u8(string m3u8Content, string m3u8Url)
        {
            List<string> tsUrls = new List<string>();
            string baseUrl = m3u8Url.Substring(0, m3u8Url.LastIndexOf('/') + 1);

            foreach (string line in m3u8Content.Split(new[] { '\n' }, StringSplitOptions.None))
            {
                string trimmedLine = line.Trim();
                if (!string.IsNullOrEmpty(trimmedLine) && !trimmedLine.StartsWith("#"))
                {
                    if (trimmedLine.StartsWith("http"))
                    {
                        tsUrls.Add(trimmedLine);
                    }
                    else
                    {
                        tsUrls.Add(baseUrl + trimmedLine);
                    }
                }
            }

            return tsUrls;
        }

        private async Task<List<string>> DownloadTsFiles(List<string> tsUrls, string detailId)
        {
            List<string> tsFiles = new List<string>();

            for (int i = 0; i < tsUrls.Count; i++)
            {
                string tsUrl = tsUrls[i];
                LogMessage?.Invoke(this, $"HLS下载: 下载分片 {i + 1}/{tsUrls.Count}");

                byte[] tsData = await DownloadBytesAsync(tsUrl);
                string tsPath = Path.Combine(_tempPath, $"{detailId}.part{i:03d}.ts");

                File.WriteAllBytes(tsPath, tsData);
                tsFiles.Add(tsPath);

                // 更新进度
                int progress = (int)((i + 1) * 100.0 / tsUrls.Count);
                ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                {
                    FileName = $"{detailId}_hls.mp4",
                    Progress = progress,
                    BytesReceived = (i + 1) * 1024 * 1024, // 估算
                    TotalBytesToReceive = tsUrls.Count * 1024 * 1024 // 估算
                });
            }

            return tsFiles;
        }

        private async Task<string> MergeTsFiles(List<string> tsFiles, string detailId, string strFileName = "")
        {
            string strFile = string.Empty;
            if(string.IsNullOrEmpty(strFileName))
            {
                strFile = detailId;
            }
            else
            {
                strFile = strFileName;
            }

            string outputPath = Path.Combine(_downloadPath, $"{strFile}_hls.mp4");

            using (var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            {
                foreach (string tsFile in tsFiles)
                {
                    byte[] tsData = File.ReadAllBytes(tsFile);
                    await outputStream.WriteAsync(tsData, 0, tsData.Length);
                    File.Delete(tsFile);
                }
            }

            return outputPath;
        }

        private string FormatFileSize(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{(bytes / 1024.0):F2} KB";
            if (bytes < 1024 * 1024 * 1024)
                return $"{(bytes / (1024.0 * 1024)):F2} MB";
            return $"{(bytes / (1024.0 * 1024 * 1024)):F2} GB";
        }
    }
}
