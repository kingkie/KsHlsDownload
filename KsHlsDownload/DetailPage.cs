using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace KsHlsDownload
{
    public class DetailPage
    {
        private const int TimeoutSeconds = 30;
        private const int MaxRetry = 3;

        private readonly string _userAgent;
        private readonly string _origin;
        private readonly string _referer;

        public event EventHandler<string> LogMessage;

        public DetailPage(bool isAppUrl = true)
        {
            if (isAppUrl)
            {
                // APP headers (用于长视频链接)
                _userAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1";
                _origin = "https://v.m.chenzhongtech.com";
                _referer = "https://v.m.chenzhongtech.com/";
            }
            else
            {
                // PC headers (用于普通网页链接)
                _userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
                _origin = "https://www.kuaishou.com";
                _referer = "https://www.kuaishou.com/new-reco";
            }

            // 设置 TLS 协议支持
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
            ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;
        }

        public async Task<string> RunAsync(string url, string cookie = "")
        {
            string result = await RequestUrlAsync(url, cookie);
            if (string.IsNullOrEmpty(result))
            {
                OnLogMessage("HTTP请求获取页面失败或返回空内容");
                // 尝试使用不同的 headers 重试
                result = await RequestUrlWithAlternativeHeadersAsync(url, cookie);
            }
            return result;
        }

        private async Task<string> RequestUrlAsync(string url, string cookie, int retryCount = 0)
        {
            try
            {
                HttpWebRequest request = CreateRequest(url, cookie);

                using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync())
                using (Stream responseStream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(responseStream))
                {
                    return await reader.ReadToEndAsync();
                }
            }
            catch (Exception ex)
            {
                OnLogMessage($"HTTP请求失败 (尝试 {retryCount + 1}/{MaxRetry}): {ex.Message}");

                if (retryCount < MaxRetry - 1)
                {
                    await Task.Delay(1000 * (retryCount + 1));
                    return await RequestUrlAsync(url, cookie, retryCount + 1);
                }

                return null;
            }
        }

        private async Task<string> RequestUrlWithAlternativeHeadersAsync(string url, string cookie)
        {
            OnLogMessage("尝试使用备用headers...");

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = TimeoutSeconds * 1000;
                request.ReadWriteTimeout = TimeoutSeconds * 1000;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                // 使用不同的 User-Agent
                request.UserAgent = "Mozilla/5.0 (Linux; Android 10; SM-G970F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/92.0.4515.131 Mobile Safari/537.36";
                request.Headers["Origin"] = "https://v.m.chenzhongtech.com";
                request.Referer = "https://v.m.chenzhongtech.com/fw/long-video";
                request.Accept = "*/*";
                request.Headers["Accept-Language"] = "zh-CN,zh;q=0.9,en;q=0.8";
                request.Headers["Accept-Encoding"] = "gzip, deflate, br";

                if (!string.IsNullOrEmpty(cookie))
                {
                    request.Headers["Cookie"] = cookie;
                }

                using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync())
                using (Stream responseStream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(responseStream))
                {
                    string content = await reader.ReadToEndAsync();
                    OnLogMessage("备用headers请求成功");
                    return content;
                }
            }
            catch (Exception ex)
            {
                OnLogMessage($"备用headers请求也失败: {ex.Message}");
                return null;
            }
        }

        private HttpWebRequest CreateRequest(string url, string cookie)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = TimeoutSeconds * 1000;
            request.ReadWriteTimeout = TimeoutSeconds * 1000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.AllowAutoRedirect = true;
            request.MaximumAutomaticRedirections = 5;

            request.UserAgent = _userAgent;
            request.Headers["Origin"] = _origin;
            request.Referer = _referer;
            request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8";
            request.Headers["Accept-Language"] = "zh-CN,zh;q=0.9";

            if (!string.IsNullOrEmpty(cookie))
            {
                request.Headers["Cookie"] = cookie;
            }

            return request;
        }

        private void OnLogMessage(string message)
        {
            LogMessage?.Invoke(this, message);
        }
    }
}
