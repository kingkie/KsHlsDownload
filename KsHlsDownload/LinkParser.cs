using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.UI;

namespace KsHlsDownload
{
    public class LinkParser
    {
        private const int TimeoutSeconds = 30;
        
        private static readonly Regex ShortUrlRegex = new Regex(
            @"https?://\S*kuaishou\.(?:com|cn)/f/[^\s/""<>\\^`{|}，。；！？、【】《》]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        private static readonly Regex VShortUrlRegex = new Regex(
            @"https?://v\.kuaishou\.(?:com|cn)/[^\s/""<>\\^`{|}，。；！？、【】《》]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        private static readonly Regex PcDetailUrlRegex = new Regex(
            @"https?://\S*kuaishou\.(?:com|cn)/short-video/\S+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        private static readonly Regex ChenzhongtechUrlRegex = new Regex(
            @"https?://\S*chenzhongtech\.(?:com|cn)/fw/photo/\S+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        private static readonly Regex CDetailUrlRegex = new Regex(
            @"https?://\S*kuaishou\.(?:com|cn)/fw/photo/\S+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        private static readonly Regex UrlRegex = new Regex(
            @"https?://[^\s""<>\\^`{|}，。；！？、【】《》]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        public List<string> ExtractUrls(string text)
        {
            var urls = new List<string>();
            var matches = UrlRegex.Matches(text);
            
            foreach (Match match in matches)
            {
                urls.Add(match.Value);
            }
            
            return urls;
        }

        public string ResolveShortUrl(string url)
        {
            return ResolveShortUrl(url, string.Empty);
        }

        public string CleanUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return url;
            }

            // 去除可能的反引号、引号和空格
            return url.Trim().Trim('`').Trim('\'').Trim('"');
        }

        public string ResolveShortUrl(string url, string cookie)
        {
            try
            {
                if (ShortUrlRegex.IsMatch(url) || VShortUrlRegex.IsMatch(url))
                {
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.AllowAutoRedirect = false;
                    request.Method = "GET";
                    request.Timeout = TimeoutSeconds * 1000;
                    request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
                    
                    if (!string.IsNullOrEmpty(cookie))
                    {
                        request.Headers["Cookie"] = cookie;
                    }

                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        if (response.StatusCode == HttpStatusCode.Redirect ||
                            response.StatusCode == HttpStatusCode.MovedPermanently ||
                            response.StatusCode == HttpStatusCode.Found ||
                            response.StatusCode == HttpStatusCode.SeeOther)
                        {
                            string location = response.Headers["Location"];
                            if (!string.IsNullOrEmpty(location))
                            {
                                return location;
                            }
                        }
                        
                        return response.ResponseUri?.ToString() ?? url;
                    }
                }
            }
            catch
            {
                // 如果解析失败，返回原始URL
            }
            
            return url;
        }

        public bool IsValidKuaishouUrl(string url)
        {
            return PcDetailUrlRegex.IsMatch(url) || 
                   ChenzhongtechUrlRegex.IsMatch(url) ||
                   CDetailUrlRegex.IsMatch(url) ||
                   ShortUrlRegex.IsMatch(url) ||
                   VShortUrlRegex.IsMatch(url);
        }

        public ParsedUrl ExtractParams(string url)
        {
            try
            {
                var uri = new Uri(url);
                var parsedUrl = new ParsedUrl();

                if (uri.Host.Contains("chenzhongtech"))
                {
                    parsedUrl.IsWeb = false;
                    parsedUrl.UserId = GetQueryParam(uri, "userId");
                    parsedUrl.DetailId = GetQueryParam(uri, "photoId");
                }
                else if (uri.AbsolutePath.Contains("/short-video/") || uri.AbsolutePath.Contains("/fw/photo/"))
                {
                    parsedUrl.IsWeb = true;
                    string[] parts = uri.AbsolutePath.Split('/');
                    parsedUrl.DetailId = parts[parts.Length - 1];
                }

                return parsedUrl;
            }
            catch
            {
                return new ParsedUrl();
            }
        }

        public string ExtractDetailId(string url)
        {
            var parsed = ExtractParams(url);
            return parsed.DetailId;
        }

        private string GetQueryParam(Uri uri, string paramName)
        {
            string query = uri.Query.TrimStart('?');

            Console.Write(string.Format("L152,查询数据：{0}",query));
            string[] pairs = query.Split('&');
            
            foreach (string pair in pairs)
            {
                string[] keyValue = pair.Split('=');
                if (keyValue.Length == 2 && keyValue[0] == paramName)
                {
                    return Uri.UnescapeDataString(keyValue[1]);
                }
            }
            
            return string.Empty;
        }

        /// <summary>
        /// 从URL中提取文件名（去除域名和参数）
        /// 例如: https://tymov2.a.kwimgs.com/upic/2026/05/20/15/BMjAy...mp4?clientCacheKey=... -> BMjAy...mp4
        /// </summary>
        public string ExtractFileNameFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return string.Empty;
            }

            try
            {
                // 清理 URL
                url = CleanUrl(url);

                // 去除参数部分
                int paramIndex = url.IndexOf('?');
                if (paramIndex > 0)
                {
                    url = url.Substring(0, paramIndex);
                }

                // 获取路径最后一部分（文件名）
                Uri uri = new Uri(url);
                string fileName = Path.GetFileName(uri.AbsolutePath);

                return fileName;
            }
            catch
            {
                // 如果解析失败，尝试手动提取
                int lastSlash = url.LastIndexOf('/');
                if (lastSlash >= 0 && lastSlash < url.Length - 1)
                {
                    string fileName = url.Substring(lastSlash + 1);
                    // 去除参数
                    int questionMark = fileName.IndexOf('?');
                    if (questionMark > 0)
                    {
                        fileName = fileName.Substring(0, questionMark);
                    }
                    return fileName;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 检查URL是否是视频文件（基于扩展名）
        /// </summary>
        public bool IsVideoUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }

            string fileName = ExtractFileNameFromUrl(url);
            string extension = Path.GetExtension(fileName).ToLowerInvariant();

            return extension == ".mp4" || extension == ".m3u8" ||
                   extension == ".ts" || extension == ".webm" ||
                   extension == ".mkv" || extension == ".avi";
        }
    }

    public class ParsedUrl
    {
        public bool IsWeb { get; set; }
        public string UserId { get; set; }
        public string DetailId { get; set; }
    }
}
