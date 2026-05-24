using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KsHlsDownload
{
    public class HtmlExtractor
    {
        private const int TimeoutSeconds = 30;
        private const string WebKeyword = "window.__APOLLO_STATE__=";
        private const string AppKeyword = "window.INIT_STATE = ";

        public event EventHandler<string> LogMessage;

        public async System.Threading.Tasks.Task<ExtractedData> ExtractAsync(string url, string cookie = "")
        {
            try
            {
                // 使用 DetailPage 获取页面内容（类似 KS-Downloader 的实现）
                bool isAppUrl = url.Contains("chenzhongtech");
                var detailPage = new DetailPage(isAppUrl);
                detailPage.LogMessage += (sender, msg) => OnLogMessage($"DetailPage: {msg}");

                string html = await detailPage.RunAsync(url, cookie);
                if (string.IsNullOrEmpty(html))
                {
                    OnLogMessage("获取网页内容失败");
                    return null;
                }

                return ParseHtml(html, url);
            }
            catch (Exception ex)
            {
                OnLogMessage($"提取数据失败: {ex.Message}");
                return null;
            }
        }

        private async System.Threading.Tasks.Task<string> GetHtmlAsync(string url, string cookie)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = TimeoutSeconds * 1000;
            request.ReadWriteTimeout = TimeoutSeconds * 1000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            // 根据URL类型设置不同的headers
            bool isAppUrl = url.Contains("chenzhongtech");

            if (isAppUrl)
            {
                // APP headers (用于长视频链接)
                request.UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1";
                request.Headers["Origin"] = "https://v.m.chenzhongtech.com";
                request.Referer = "https://v.m.chenzhongtech.com/";
            }
            else
            {
                // PC headers (用于普通网页链接)
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
                request.Headers["Origin"] = "https://www.kuaishou.com";
                request.Referer = "https://www.kuaishou.com/new-reco";
            }

            request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8";
            request.Headers["Accept-Language"] = "zh-CN,zh;q=0.9";

            if (!string.IsNullOrEmpty(cookie))
            {
                request.Headers["Cookie"] = cookie;
            }

            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream))
            {
                return await reader.ReadToEndAsync();
            }
        }

        private ExtractedData ParseHtml(string html, string url)
        {
            bool isWeb = !url.Contains("chenzhongtech");
            string keyword = isWeb ? WebKeyword : AppKeyword;

            var scriptRegex = new Regex(@"<script[^>]*>([\s\S]*?)</script>", RegexOptions.IgnoreCase);
            var matches = scriptRegex.Matches(html);

            string dataScript = null;
            foreach (Match match in matches)
            {
                string scriptContent = match.Groups[1].Value;
                if (scriptContent.Contains(keyword))
                {
                    dataScript = scriptContent;
                    break;
                }
            }

            if (dataScript == null)
            {
                OnLogMessage("未找到包含数据的脚本");
                return null;
            }

            string jsonData = CleanScriptData(dataScript, isWeb);
            if (string.IsNullOrEmpty(jsonData))
            {
                OnLogMessage("解析JSON数据失败");
                return null;
            }

            return ExtractDataFromJson(jsonData, url, isWeb);
        }

        private string CleanScriptData(string script, bool isWeb)
        {
            try
            {
                if (isWeb)
                {
                    int startIndex = script.IndexOf(WebKeyword) + WebKeyword.Length;
                    int endIndex = script.IndexOf(";(function(){var s;(s=document.currentScript||document.scripts[document.scripts.length-1]).parentNode.removeChild(s);}());", startIndex);

                    if (endIndex > startIndex)
                    {
                        return script.Substring(startIndex, endIndex - startIndex);
                    }

                    endIndex = script.IndexOf("</script>", startIndex);
                    if (endIndex > startIndex)
                    {
                        return script.Substring(startIndex, endIndex - startIndex);
                    }

                    return script.Substring(startIndex);
                }
                else
                {
                    // 匹配 "photo":{"..."}, "serialInfo" 格式
                    // 使用 Singleline 选项让 . 匹配换行符
                    var regex = new Regex(@"""photo"":(\{.*?\}),\s*""serialInfo""", RegexOptions.Singleline);
                    var match = regex.Match(script);
                    if (match.Success)
                    {
                        string photoData = match.Groups[1].Value;
                        OnLogMessage($"提取到photo数据: {photoData.Substring(0, Math.Min(200, photoData.Length))}...");
                        return photoData;
                    }

                    // 如果第一种方式失败，尝试匹配更大范围
                    var regex2 = new Regex(@"window\.INIT_STATE\s*=\s*\{([\s\S]*?)\};", RegexOptions.Singleline);
                    match = regex2.Match(script);
                    if (match.Success)
                    {
                        string initState = match.Groups[1].Value;
                        OnLogMessage($"提取到INIT_STATE数据，长度: {initState.Length}");
                        return "{" + initState + "}";
                    }
                }
            }
            catch
            {
                // 解析失败
            }

            return null;
        }

        private ExtractedData ExtractDataFromJson(string jsonData, string url, bool isWeb)
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;

                dynamic data = serializer.DeserializeObject(jsonData);

                if (isWeb)
                {
                    return ExtractWebData(data, url);
                }
                else
                {
                    return ExtractAppData(data, url);
                }
            }
            catch (Exception ex)
            {
                OnLogMessage($"解析JSON失败: {ex.Message}");
                return null;
            }
        }

        private ExtractedData ExtractWebData(dynamic data, string url)
        {
            try
            {
                var linkParser = new LinkParser();
                var parsedUrl = linkParser.ExtractParams(url);
                string detailId = parsedUrl.DetailId;

                if (string.IsNullOrEmpty(detailId))
                {
                    OnLogMessage("无法提取作品ID");
                    return null;
                }

                string key = $"VisionVideoDetailPhoto:{detailId}";

                if (data is Dictionary<string, object> dict && dict.ContainsKey("defaultClient"))
                {
                    var defaultClient = dict["defaultClient"] as Dictionary<string, object>;

                    if (defaultClient != null && defaultClient.ContainsKey(key))
                    {
                        var photoData = defaultClient[key] as Dictionary<string, object>;
                        return ParsePhotoData(photoData, detailId, true);
                    }
                }

                OnLogMessage("未找到作品数据");
                return null;
            }
            catch (Exception ex)
            {
                OnLogMessage($"提取Web数据失败: {ex.Message}");
                return null;
            }
        }

        private ExtractedData ExtractAppData(dynamic data, string url)
        {
            try
            {
                var linkParser = new LinkParser();
                var parsedUrl = linkParser.ExtractParams(url);
                string detailId = parsedUrl.DetailId;

                if (data is Dictionary<string, object> dict)
                {
                    // 调试：输出提取到的所有字段
                    OnLogMessage($"APP数据字段: {string.Join(", ", dict.Keys)}");

                    // 检查是否有download字段
                    if (dict.ContainsKey("download"))
                    {
                        var downloadObj = dict["download"];
                        OnLogMessage($"download字段类型: {downloadObj?.GetType().Name}");
                        if (downloadObj is List<object>)
                        {
                            var list = (List<object>)downloadObj;
                            OnLogMessage($"download列表长度: {list.Count}");
                            if (list.Count > 0)
                            {
                                OnLogMessage($"download[0]: {list[0]?.ToString()?.Substring(0, Math.Min(100, list[0]?.ToString()?.Length ?? 0))}...");
                            }
                        }
                        else if (downloadObj is string)
                        {
                            OnLogMessage($"download字符串: {downloadObj.ToString()?.Substring(0, Math.Min(100, downloadObj.ToString()?.Length ?? 0))}...");
                        }
                    }

                    return ParsePhotoData(dict, detailId, false);
                }
                else
                {
                    OnLogMessage($"数据类型不是Dictionary: {data?.GetType().Name}");
                }

                OnLogMessage("未找到作品数据");
                return null;
            }
            catch (Exception ex)
            {
                OnLogMessage($"提取App数据失败: {ex.Message}");
                return null;
            }
        }

        private ExtractedData ParsePhotoData(Dictionary<string, object> photoData, string detailId, bool isWeb)
        {
            var result = new ExtractedData
            {
                DetailId = detailId,
                CollectionTime = DateTime.Now.ToString("yyyy-MM-dd_HH:mm:ss")
            };

            try
            {
                // 调试：输出所有可用的键
                string allKeys = string.Join(", ", photoData.Keys);
                OnLogMessage($"可用字段: {allKeys}");

                if (photoData.ContainsKey("caption"))
                {
                    result.Caption = photoData["caption"]?.ToString();
                }

                if (photoData.ContainsKey("coverUrl"))
                {
                    result.CoverUrl = photoData["coverUrl"]?.ToString();
                }

                if (photoData.ContainsKey("duration"))
                {
                    object durationObj = photoData["duration"];
                    if (durationObj is int)
                    {
                        result.Duration = TimeConversion((int)durationObj);
                    }
                    else
                    {
                        result.Duration = photoData["duration"]?.ToString() ?? "00:00:00";
                    }
                }

                if (photoData.ContainsKey("realLikeCount"))
                {
                    result.LikeCount = Convert.ToInt64(photoData["realLikeCount"] ?? -1);
                }

                // 尝试各种方式提取下载链接
                bool downloadFound = false;

                // 方式1: photoUrl 字段
                if (!downloadFound && photoData.ContainsKey("photoUrl"))
                {
                    string photoUrl = photoData["photoUrl"]?.ToString();
                    if (!string.IsNullOrEmpty(photoUrl))
                    {
                        result.DownloadUrls = new List<string> { photoUrl };
                        result.PhotoType = "视频";
                        downloadFound = true;
                        OnLogMessage("找到下载链接 (photoUrl)");
                    }
                }

                // 方式2: download 字段
                if (!downloadFound && photoData.ContainsKey("download"))
                {
                    var downloadObj = photoData["download"];
                    if (downloadObj is string)
                    {
                        string downloadUrl = downloadObj.ToString();
                        if (!string.IsNullOrEmpty(downloadUrl))
                        {
                            result.DownloadUrls = new List<string> { downloadUrl };
                            downloadFound = true;
                            OnLogMessage("找到下载链接 (download string)");
                        }
                    }
                    else if (downloadObj is List<object>)
                    {
                        var urls = ((List<object>)downloadObj).ConvertAll(o => o?.ToString());
                        urls.RemoveAll(string.IsNullOrEmpty);
                        if (urls.Count > 0)
                        {
                            result.DownloadUrls = urls;
                            downloadFound = true;
                            OnLogMessage($"找到下载链接 (download list), 数量: {urls.Count}");
                        }
                    }
                }

                // 方式3: 长视频 - ext_params 字段
                if (!downloadFound && photoData.ContainsKey("ext_params"))
                {
                    var extParamsObj = photoData["ext_params"];
                    Dictionary<string, object> extParams = null;

                    // ext_params 可能是字符串或字典
                    if (extParamsObj is string extParamsStr)
                    {
                        try
                        {
                            var serializer = new JavaScriptSerializer();
                            extParams = serializer.Deserialize<Dictionary<string, object>>(extParamsStr);
                        }
                        catch (Exception ex)
                        {
                            OnLogMessage($"解析 ext_params 字符串失败: {ex.Message}");
                        }
                    }
                    else if (extParamsObj is Dictionary<string, object>)
                    {
                        extParams = extParamsObj as Dictionary<string, object>;
                    }

                    if (extParams != null)
                    {
                        OnLogMessage($"ext_params 字段: {string.Join(", ", extParams.Keys)}");

                        // 检查是否是单图/单视频
                        if (extParams.ContainsKey("single") && extParams["single"] != null)
                        {
                            string single = extParams["single"].ToString().ToLower();
                            if (single == "true" || single == "1")
                            {
                                // 单图/单视频，使用封面URL
                                if (!string.IsNullOrEmpty(result.CoverUrl))
                                {
                                    result.DownloadUrls = new List<string> { result.CoverUrl };
                                    result.PhotoType = "图片";
                                    downloadFound = true;
                                    OnLogMessage("找到下载链接 (single=true, 使用封面URL)");
                                }
                            }
                        }

                        // 如果不是单图，尝试从 atlas 提取
                        if (!downloadFound && extParams.ContainsKey("atlas"))
                        {
                            var atlasObj = extParams["atlas"];
                            Dictionary<string, object> atlas = null;

                            if (atlasObj is string atlasStr)
                            {
                                try
                                {
                                    var serializer = new JavaScriptSerializer();
                                    atlas = serializer.Deserialize<Dictionary<string, object>>(atlasStr);
                                }
                                catch (Exception ex)
                                {
                                    OnLogMessage($"解析 atlas 字符串失败: {ex.Message}");
                                }
                            }
                            else if (atlasObj is Dictionary<string, object>)
                            {
                                atlas = atlasObj as Dictionary<string, object>;
                            }

                            if (atlas != null)
                            {
                                OnLogMessage($"atlas 字段: {string.Join(", ", atlas.Keys)}");

                                if (atlas.ContainsKey("cdn") && atlas.ContainsKey("list"))
                                {
                                    var cdnList = atlas["cdn"] as List<object>;
                                    var list = atlas["list"] as List<object>;

                                    if (cdnList != null && cdnList.Count > 0 && list != null && list.Count > 0)
                                    {
                                        string cdn = cdnList[0]?.ToString();
                                        result.DownloadUrls = new List<string>();
                                        foreach (var item in list)
                                        {
                                            string itemUrl = item?.ToString();
                                            if (!string.IsNullOrEmpty(itemUrl))
                                            {
                                                string fullUrl = itemUrl.StartsWith("http") ? itemUrl : $"https://{cdn}{itemUrl}";
                                                result.DownloadUrls.Add(fullUrl);
                                            }
                                        }

                                        if (result.DownloadUrls.Count > 0)
                                        {
                                            result.PhotoType = "视频";
                                            downloadFound = true;
                                            OnLogMessage($"找到下载链接 (atlas), 数量: {result.DownloadUrls.Count}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // 方式4: 尝试从 photoUrls 提取
                if (!downloadFound && photoData.ContainsKey("photoUrls"))
                {
                    var photoUrlsObj = photoData["photoUrls"];
                    if (photoUrlsObj is List<object>)
                    {
                        var urls = new List<string>();
                        foreach (var o in (List<object>)photoUrlsObj)
                        {
                            string url = o?.ToString();
                            if (!string.IsNullOrEmpty(url))
                            {
                                url = CleanUrl(url);
                                urls.Add(url);
                            }
                        }
                        urls.RemoveAll(string.IsNullOrEmpty);
                        if (urls.Count > 0)
                        {
                            result.DownloadUrls = urls;
                            result.PhotoType = "图片";
                            downloadFound = true;
                            OnLogMessage($"找到下载链接 (photoUrls), 数量: {urls.Count}");
                        }
                    }
                }

                // 方式5: 尝试从 mainMvUrls 提取 (长视频)
                if (!downloadFound && photoData.ContainsKey("mainMvUrls"))
                {
                    var mainMvUrlsObj = photoData["mainMvUrls"];
                    OnLogMessage($"mainMvUrls 字段类型: {mainMvUrlsObj?.GetType().Name}");

                    // 处理数组类型 (Object[] 或 List<object>)
                    if (mainMvUrlsObj is System.Collections.IList arrayObj)
                    {
                        var urls = new List<string>();
                        OnLogMessage($"mainMvUrls 数组长度: {arrayObj.Count}");

                        int itemIndex = 0;
                        foreach (var item in arrayObj)
                        {
                            itemIndex++;
                            OnLogMessage($"=== mainMvUrls[{itemIndex}] 类型: {item?.GetType().Name} ===");

                            if (item is Dictionary<string, object> urlDict)
                            {
                                // 打印所有键值对
                                OnLogMessage($"--- mainMvUrls[{itemIndex}] 包含 {urlDict.Count} 个字段 ---");
                                foreach (var kvp in urlDict)
                                {
                                    string valueStr = kvp.Value?.ToString() ?? "null";
                                    if (valueStr.Length > 200)
                                    {
                                        valueStr = valueStr.Substring(0, 200) + "...";
                                    }
                                    OnLogMessage($"    [{kvp.Key}]: {valueStr}");
                                }

                                // 尝试获取 url 字段
                                if (urlDict.ContainsKey("url"))
                                {
                                    string url = urlDict["url"]?.ToString();
                                    if (!string.IsNullOrEmpty(url))
                                    {
                                        url = CleanUrl(url);
                                        urls.Add(url);
                                        OnLogMessage($">>> 找到url: {url.Substring(0, Math.Min(150, url.Length))}...");
                                    }
                                }
                                // 或者 urlList 中的 URL
                                else if (urlDict.ContainsKey("urlList"))
                                {
                                    OnLogMessage($"--- 发现 urlList 字段 ---");
                                    var urlList = urlDict["urlList"] as System.Collections.IList;
                                    if (urlList != null)
                                    {
                                        int urlIndex = 0;
                                        foreach (var urlItem in urlList)
                                        {
                                            urlIndex++;
                                            if (urlItem is Dictionary<string, object> urlItemDict)
                                            {
                                                foreach (var urlKvp in urlItemDict)
                                                {
                                                    string urlValue = urlKvp.Value?.ToString() ?? "null";
                                                    if (urlValue.Length > 200)
                                                    {
                                                        urlValue = urlValue.Substring(0, 200) + "...";
                                                    }
                                                    OnLogMessage($"    urlList[{urlIndex}][{urlKvp.Key}]: {urlValue}");
                                                }

                                                if (urlItemDict.ContainsKey("url"))
                                                {
                                                    string url = urlItemDict["url"]?.ToString();
                                                    if (!string.IsNullOrEmpty(url))
                                                    {
                                                        url = CleanUrl(url);
                                                        urls.Add(url);
                                                        OnLogMessage($">>> 找到urlList url: {url.Substring(0, Math.Min(150, url.Length))}...");
                                                    }
                                                }
                                            }
                                            else if (urlItem != null)
                                            {
                                                OnLogMessage($"    urlList[{urlIndex}]: {urlItem}");
                                            }
                                        }
                                    }
                                }
                            }
                            else if (item != null)
                            {
                                string itemStr = item.ToString();
                                OnLogMessage($"    非字典对象: {itemStr.Substring(0, Math.Min(200, itemStr.Length))}");
                                if (itemStr.StartsWith("http"))
                                {
                                    itemStr = CleanUrl(itemStr);
                                    urls.Add(itemStr);
                                }
                            }
                        }

                        if (urls.Count > 0)
                        {
                            // 对下载链接进行去重
                            var distinctUrls = urls.Distinct().ToList();
                            if (distinctUrls.Count < urls.Count)
                            {
                                OnLogMessage($"去重前: {urls.Count} 个链接, 去重后: {distinctUrls.Count} 个链接");
                            }
                            result.DownloadUrls = distinctUrls;
                            result.PhotoType = "视频";
                            downloadFound = true;
                            OnLogMessage($"找到下载链接 (mainMvUrls), 数量: {distinctUrls.Count}");
                        }
                    }
                    else if (mainMvUrlsObj is Dictionary<string, object>)
                    {
                        var mainMvUrlsDict = (Dictionary<string, object>)mainMvUrlsObj;
                        if (mainMvUrlsDict.ContainsKey("url"))
                        {
                            string url = mainMvUrlsDict["url"]?.ToString();
                            if (!string.IsNullOrEmpty(url))
                            {
                                url = CleanUrl(url);
                                result.DownloadUrls = new List<string> { url };
                                result.PhotoType = "视频";
                                downloadFound = true;
                                OnLogMessage($"找到下载链接 (mainMvUrls.url), 数量: 1");
                            }
                        }
                    }
                }

                // 方式6: 尝试从 ext_params.video 提取
                if (!downloadFound && photoData.ContainsKey("ext_params"))
                {
                    var extParamsObj = photoData["ext_params"];
                    if (extParamsObj is Dictionary<string, object> extParamsDict && extParamsDict.ContainsKey("video"))
                    {
                        var videoObj = extParamsDict["video"];
                        OnLogMessage($"ext_params.video 字段类型: {videoObj?.GetType().Name}");

                        if (videoObj is string && !string.IsNullOrEmpty(videoObj.ToString()))
                        {
                            string url = CleanUrl(videoObj.ToString());
                            result.DownloadUrls = new List<string> { url };
                            result.PhotoType = "视频";
                            downloadFound = true;
                            OnLogMessage($"找到下载链接 (ext_params.video string)");
                        }
                        else if (videoObj is Dictionary<string, object> videoDict)
                        {
                            if (videoDict.ContainsKey("url"))
                            {
                                string url = videoDict["url"]?.ToString();
                                if (!string.IsNullOrEmpty(url))
                                {
                                    url = CleanUrl(url);
                                    result.DownloadUrls = new List<string> { url };
                                    result.PhotoType = "视频";
                                    downloadFound = true;
                                    OnLogMessage($"找到下载链接 (ext_params.video.url)");
                                }
                            }
                        }
                    }
                }

                // 方式7: 尝试从 urls 提取
                if (!downloadFound && photoData.ContainsKey("urls"))
                {
                    var urlsObj = photoData["urls"];
                    if (urlsObj is List<object>)
                    {
                        var urls = new List<string>();
                        foreach (var o in (List<object>)urlsObj)
                        {
                            string url = o?.ToString();
                            if (!string.IsNullOrEmpty(url))
                            {
                                url = CleanUrl(url);
                                urls.Add(url);
                            }
                        }
                        urls.RemoveAll(string.IsNullOrEmpty);
                        if (urls.Count > 0)
                        {
                            result.DownloadUrls = urls;
                            downloadFound = true;
                            OnLogMessage($"找到下载链接 (urls), 数量: {urls.Count}");
                        }
                    }
                }

                if (!downloadFound)
                {
                    OnLogMessage("警告: 未找到任何下载链接");
                }

                if (photoData.ContainsKey("timestamp"))
                {
                    object timestampObj = photoData["timestamp"];
                    if (timestampObj is long)
                    {
                        result.Timestamp = FormatDate((long)timestampObj);
                    }
                    else if (timestampObj is int)
                    {
                        result.Timestamp = FormatDate((int)timestampObj);
                    }
                }

                if (photoData.ContainsKey("viewCount"))
                {
                    result.ViewCount = Convert.ToInt64(photoData["viewCount"] ?? -1);
                }

                // authorID 可能是 authorID, userEid, id 等
                if (photoData.ContainsKey("authorID"))
                {
                    result.AuthorId = photoData["authorID"]?.ToString();
                }
                else if (photoData.ContainsKey("userEid"))
                {
                    result.AuthorId = photoData["userEid"]?.ToString();
                }
                else if (photoData.ContainsKey("id"))
                {
                    result.AuthorId = photoData["id"]?.ToString();
                }

                // name 可能是 name, userName 等
                if (photoData.ContainsKey("name"))
                {
                    result.AuthorName = photoData["name"]?.ToString();
                }
                else if (photoData.ContainsKey("userName"))
                {
                    result.AuthorName = photoData["userName"]?.ToString();
                }

                if (string.IsNullOrEmpty(result.PhotoType))
                {
                    result.PhotoType = "视频";
                }
            }
            catch (Exception ex)
            {
                OnLogMessage($"解析作品数据失败: {ex.Message}");
            }

            return result;
        }

        private string CleanUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return url;
            }

            // 去除可能的反引号、引号和空格
            return url.Trim().Trim('`').Trim('\'').Trim('"');
        }

        private string TimeConversion(int timeMs)
        {
            int seconds = timeMs / 1000;
            int hours = seconds / 3600;
            int minutes = (seconds % 3600) / 60;
            seconds = seconds % 60;
            return $"{hours:00}:{minutes:00}:{seconds:00}";
        }

        private string FormatDate(long timestamp)
        {
            if (timestamp > 0)
            {
                DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
                dateTime = dateTime.AddMilliseconds(timestamp).ToLocalTime();
                return dateTime.ToString("yyyy-MM-dd_HH:mm:ss");
            }
            return "unknown";
        }

        private string FormatDate(int timestamp)
        {
            return FormatDate((long)timestamp);
        }

        protected virtual void OnLogMessage(string message)
        {
            LogMessage?.Invoke(this, message);
        }
    }

    public class ExtractedData
    {
        public string DetailId { get; set; }
        public string CollectionTime { get; set; }
        public string Caption { get; set; }
        public string CoverUrl { get; set; }
        public string Duration { get; set; }
        public long LikeCount { get; set; }
        public List<string> DownloadUrls { get; set; } = new List<string>();
        public string Timestamp { get; set; }
        public long ViewCount { get; set; }
        public string AuthorId { get; set; }
        public string AuthorName { get; set; }
        public string PhotoType { get; set; }
    }
}
