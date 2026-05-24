using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace KsHlsDownload
{
    public partial class mainFrm : Form
    {
        public mainFrm()
        {
            InitializeComponent();
        }

        private string downloadPath = string.Empty;
        private string strCookie = string.Empty;
        private LinkParser linkParser = new LinkParser();
        private HlsDownloader hlsDownloader;
        private Downloader downloader;

        private HtmlExtractor htmlExtractor = new HtmlExtractor();

        private void mainFrm_Load(object sender, EventArgs e)
        {
            downloadPath = Path.Combine(Application.StartupPath, "DownFiles");
            downloader = new Downloader(downloadPath);
            hlsDownloader = new HlsDownloader(downloadPath);


            downloader.ProgressChanged += Downloader_ProgressChanged;
            downloader.DownloadComplete += Downloader_DownloadComplete;

            hlsDownloader.ProgressChanged += Downloader_ProgressChanged;
            hlsDownloader.DownloadComplete += Downloader_DownloadComplete;
            hlsDownloader.LogMessage += HlsDownloader_LogMessage;
            htmlExtractor.LogMessage += HtmlExtractor_LogMessage;
        }

        private string strFileName = string.Empty;

        private void btnStart_Click(object sender, EventArgs e)
        {
            string url = txtUrl.Text.Trim();
            txtUrl.Text = string.Empty;
            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show("请输入下载地址", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!linkParser.IsValidKuaishouUrl(url))
            {
                MessageBox.Show("请输入有效的链接", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            strFileName = string.Empty;
            btnStart.Enabled = false;
            pBar.Value = 0;
            lblStatus.Text = "状态：正在解析链接...";
            lblStatus.ForeColor = System.Drawing.Color.Blue;

            DownloadAsync(url);
        }

        private void AddLog(string message)
        {
            if (txtLog.InvokeRequired)
            {
                txtLog.Invoke(new Action(() => AddLog(message)));
                return;
            }

            txtLog.AppendText($"{DateTime.Now:HH:mm:ss} - {message}{Environment.NewLine}");
            txtLog.ScrollToCaret();
        }

        private async void DownloadAsync(string url)
        {
            try
            {
                AddLog("步骤1: 解析短链接...");
                string resolvedUrl = linkParser.ResolveShortUrl(url, strCookie);

                AddLog($"解析后的链接: {resolvedUrl}");

                var parsedUrl = linkParser.ExtractParams(resolvedUrl);
                AddLog($"作品ID: {parsedUrl.DetailId}");
                AddLog($"是否Web链接: {parsedUrl.IsWeb}");

                // 统一使用智能下载方式，程序会自动判断最佳下载方式
                AddLog("下载方式: 智能判断");
                await SmartDownloadAsync(resolvedUrl, parsedUrl.DetailId);


                // 获取下载方式
                //bool useHls = rbHls.Checked;
                //AddLog($"下载方式: {(useHls ? "HLS流媒体" : "普通下载")}");
                //string strType = string.Empty;


                //var extractedData = await htmlExtractor.ExtractAsync(resolvedUrl, strCookie);
                //if (extractedData == null)
                //{
                //    lblStatus.Text = "状态：获取作品数据失败";
                //    lblStatus.ForeColor = System.Drawing.Color.Red;
                //    //return;
                //}
                //else
                //{
                //    strType = extractedData.PhotoType;

                //    AddLog($"作品标题: {extractedData.Caption}");
                //    AddLog($"作品类型: {extractedData.PhotoType}");
                //    AddLog($"作者: {extractedData.AuthorName}");
                //    AddLog($"时长: {extractedData.Duration}");
                //    AddLog($"点赞数: {extractedData.LikeCount}");
                //    AddLog($"播放量: {extractedData.ViewCount}");
                //}
                //Console.WriteLine("视频类型：" + strType);

                //if (false)
                //{
                //    // 使用HLS下载方式
                //    await DownloadWithHlsAsync(resolvedUrl, parsedUrl.DetailId);
                //}
                //else
                //{
                //    // 使用普通下载方式
                //    await DownloadWithNormalAsync(resolvedUrl, parsedUrl.DetailId);
                //}
            }
            catch (Exception ex)
            {
                lblStatus.Text = "状态：下载失败";
                lblStatus.ForeColor = System.Drawing.Color.Red;
                AddLog($"下载失败: {ex.Message}");
                AddLog($"异常详情: {ex.ToString()}");
            }
            finally
            {
                btnStart.Enabled = true;
            }
        }

        /// <summary>
        /// 智能下载方式：自动判断使用普通下载还是HLS下载
        /// </summary>
        private async Task SmartDownloadAsync(string resolvedUrl, string detailId)
        {
            AddLog("步骤2: 获取作品数据...");
            lblStatus.Text = "状态：正在获取作品数据...";

            var extractedData = await htmlExtractor.ExtractAsync(resolvedUrl, strCookie);
            if (extractedData == null)
            {
                lblStatus.Text = "状态：获取作品数据失败";
                lblStatus.ForeColor = System.Drawing.Color.Red;
                return;
            }

            AddLog($"作品标题: {extractedData.Caption}");
            AddLog($"作品类型: {extractedData.PhotoType}");
            AddLog($"作者: {extractedData.AuthorName}");
            AddLog($"时长: {extractedData.Duration}");
            AddLog($"点赞数: {extractedData.LikeCount}");
            AddLog($"播放量: {extractedData.ViewCount}");

            string strCaption = FilterDangerousChars(extractedData.Caption); //Regex.Replace(extractedData.Caption, @"[^a-zA-Z0-9\u4e00-\u9fa5]", "");

            if(!string.IsNullOrEmpty(strCaption))
            {
                strFileName = strCaption;
            }

            // 检查是否是图片
            if (extractedData.PhotoType == "图片")
            {
                AddLog("作品是图片，使用普通下载...");
                await DownloadUrls(extractedData);
                return;
            }

            // 检查是否有有效的普通下载链接，并进行去重
            bool hasValidDownloadLink = false;
            if (extractedData.DownloadUrls != null && extractedData.DownloadUrls.Count > 0)
            {
                // 使用文件名（去除域名和参数）进行去重
                var uniqueFileNames = new HashSet<string>();
                var deduplicatedUrls = new List<string>();

                foreach (string url in extractedData.DownloadUrls)
                {
                    if (!string.IsNullOrEmpty(url) && url.StartsWith("http"))
                    {
                        string fileName = linkParser.ExtractFileNameFromUrl(url);
                        if (!string.IsNullOrEmpty(fileName) && uniqueFileNames.Add(fileName))
                        {
                            deduplicatedUrls.Add(url);
                        }
                    }
                }

                if (deduplicatedUrls.Count > 0)
                {
                    hasValidDownloadLink = true;
                    extractedData.DownloadUrls = deduplicatedUrls;

                    if (deduplicatedUrls.Count < extractedData.DownloadUrls.Count)
                    {
                        AddLog($"去重前: {extractedData.DownloadUrls.Count} 个链接, 去重后: {deduplicatedUrls.Count} 个链接");
                    }
                }
            }

            if (hasValidDownloadLink)
            {
                AddLog($"找到 {extractedData.DownloadUrls.Count} 个下载链接，使用普通下载...");
                await DownloadUrls(extractedData);
                return;
            }

            // 没有普通下载链接，尝试HLS下载
            AddLog("未找到直接下载链接，尝试使用HLS流媒体下载...");
            AddLog($"下载链接: {resolvedUrl}");
            lblStatus.Text = "状态：正在获取HLS流...";

            if (!string.IsNullOrEmpty(strCookie))
            {
                hlsDownloader.Cookie = strCookie;
            }

            try
            {
                await hlsDownloader.DownloadAsync(resolvedUrl, detailId);
            }
            catch (Exception ex)
            {
                AddLog("所有下载方式都失败了！");
                AddLog("可能的原因：");
                AddLog("  1. 网络连接问题");
                AddLog("  2. 该视频需要特定的权限或登录");
                AddLog("  3. 服务器限制了该视频的下载");
                AddLog("  4. 该视频的下载方式已更新");
                AddLog($"错误详情: {ex.Message}");
                lblStatus.Text = "状态：下载失败";
                lblStatus.ForeColor = System.Drawing.Color.Red;
                throw;
            }
        }

        /// <summary>
        /// 下载普通链接
        /// </summary>
        private async Task DownloadUrls(ExtractedData extractedData)
        {
            AddLog($"找到 {extractedData.DownloadUrls.Count} 个下载链接");
            foreach (string downloadUrl in extractedData.DownloadUrls)
            {
                AddLog($"下载链接: {downloadUrl}");
            }

            AddLog("步骤3: 开始下载...");
            lblStatus.Text = "状态：正在下载...";

            if (!string.IsNullOrEmpty(strCookie))
            {
                downloader.Cookie = strCookie;
            }

            int successCount = 0;
            int totalCount = extractedData.DownloadUrls.Count;

            for (int i = 0; i < extractedData.DownloadUrls.Count; i++)
            {
                string downloadUrl = extractedData.DownloadUrls[i];
                string fileName = GenerateFileName(extractedData, i);

                AddLog($"下载文件 {i + 1}/{totalCount}: {fileName}");
                await downloader.DownloadFileAsync(downloadUrl, fileName);

                if (linkParser.IsVideoUrl(downloadUrl))
                {
                    successCount++;
                    AddLog($"下载完成: {fileName}");
                }
            }

            if (successCount > 0)
            {
                lblStatus.Text = $"状态：下载完成 ({successCount}/{totalCount})";
                lblStatus.ForeColor = System.Drawing.Color.Green;
                AddLog("下载成功！");
                pBar.Value = pBar.Maximum;
                lblProgress.Text = $"进度：100%";
            }
            else
            {
                lblStatus.Text = "状态：未下载视频";
                lblStatus.ForeColor = System.Drawing.Color.Orange;
                AddLog("未找到有效的视频文件");
            }
        }

        private async Task DownloadWithHlsAsync(string resolvedUrl, string detailId)
        {
            AddLog("步骤2: 使用HLS流媒体下载...");
            AddLog($"下载链接: {resolvedUrl}");
            lblStatus.Text = "状态：正在获取HLS流...";

            if (!string.IsNullOrEmpty(strCookie))
            {
                hlsDownloader.Cookie = strCookie;
            }

            var extractedData = await htmlExtractor.ExtractAsync(resolvedUrl, strCookie);
            if (extractedData == null)
            {
                lblStatus.Text = "状态：获取作品数据失败";
                lblStatus.ForeColor = System.Drawing.Color.Red;
                //return;
            }
            else
            {
                AddLog($"作品标题: {extractedData.Caption}");
                AddLog($"作品类型: {extractedData.PhotoType}");
                AddLog($"作者: {extractedData.AuthorName}");
                AddLog($"时长: {extractedData.Duration}");
                AddLog($"点赞数: {extractedData.LikeCount}");
                AddLog($"播放量: {extractedData.ViewCount}");
            }

            await hlsDownloader.DownloadAsync(resolvedUrl, detailId);
        }

        private async Task DownloadWithNormalAsync(string resolvedUrl, string detailId)
        {
            AddLog("步骤2: 获取作品数据...");
            lblStatus.Text = "状态：正在获取作品数据...";

            var extractedData = await htmlExtractor.ExtractAsync(resolvedUrl, strCookie);
            if (extractedData == null)
            {
                lblStatus.Text = "状态：获取作品数据失败";
                lblStatus.ForeColor = System.Drawing.Color.Red;
                return;
            }

            AddLog($"作品标题: {extractedData.Caption}");
            AddLog($"作品类型: {extractedData.PhotoType}");
            AddLog($"作者: {extractedData.AuthorName}");
            AddLog($"时长: {extractedData.Duration}");
            AddLog($"点赞数: {extractedData.LikeCount}");
            AddLog($"播放量: {extractedData.ViewCount}");

            if (extractedData.DownloadUrls == null || extractedData.DownloadUrls.Count == 0)
            {
                lblStatus.Text = "状态：未找到下载链接";
                lblStatus.ForeColor = System.Drawing.Color.Red;
                AddLog("错误：未找到下载链接");
                return;
            }

            AddLog($"找到 {extractedData.DownloadUrls.Count} 个下载链接");
            foreach (string downloadUrl in extractedData.DownloadUrls)
            {
                AddLog($"下载链接: {downloadUrl}");
            }

            AddLog("步骤3: 开始下载...");
            lblStatus.Text = "状态：正在下载...";

            if (!string.IsNullOrEmpty(strCookie))
            {
                downloader.Cookie = strCookie;
            }

            int successCount = 0;
            int totalCount = extractedData.DownloadUrls.Count;

            for (int i = 0; i < extractedData.DownloadUrls.Count; i++)
            {
                string downloadUrl = extractedData.DownloadUrls[i];
                string fileName = GenerateFileName(extractedData, i);

                AddLog($"下载文件 {i + 1}/{totalCount}: {fileName}");
                await downloader.DownloadFileAsync(downloadUrl, fileName);

                if (downloadUrl.EndsWith(".mp4"))
                {
                    successCount++;
                }
            }

            if (successCount > 0)
            {
                lblStatus.Text = "状态：下载完成";
                lblStatus.ForeColor = System.Drawing.Color.Green;
                AddLog($"成功下载 {successCount}/{totalCount} 个文件");
            }
            else
            {
                lblStatus.Text = "状态：下载失败";
                lblStatus.ForeColor = System.Drawing.Color.Red;
            }
        }

        //================================
        private string GenerateFileName(ExtractedData data, int index)
        {

            string baseName = string.Empty;//  !string.IsNullOrEmpty(data.DetailId) ? data.DetailId : "download";
            if(!string.IsNullOrEmpty(strFileName))
            {
                baseName = strFileName;
            }
            else
            {
                baseName = !string.IsNullOrEmpty(data.DetailId) ? data.DetailId : "download";
            }

            if (data.DownloadUrls != null && data.DownloadUrls.Count > 1)
            {
                return $"{baseName}_{index + 1}.mp4";
            }

            return $"{baseName}.mp4";
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

        private void Downloader_ProgressChanged(object sender, DownloadProgressEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => Downloader_ProgressChanged(sender, e)));
                return;
            }

            pBar.Value = e.Progress;
            lblProgress.Text = $"进度：{e.Progress}% ";//({FormatFileSize(e.BytesReceived)} / {FormatFileSize(e.TotalBytesToReceive)}) 
        }

        private void Downloader_DownloadComplete(object sender, DownloadCompleteEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => Downloader_DownloadComplete(sender, e)));
                return;
            }

            if (e.Success)
            {
                lblStatus.Text = "状态：下载完成";
                lblStatus.ForeColor = System.Drawing.Color.Green;
                AddLog($"下载完成: {e.FilePath}");
            }
            else
            {
                lblStatus.Text = "状态：下载失败";
                lblStatus.ForeColor = System.Drawing.Color.Red;
                AddLog($"下载失败: {e.ErrorMessage}");
            }
        }

        private void HlsDownloader_LogMessage(object sender, string message)
        {
            AddLog($"HLS下载器: {message}");
        }

        private void HtmlExtractor_LogMessage(object sender, string message)
        {
            AddLog($"提取器: {message}");
        }

        public string FilterDangerousChars(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;
            input = Regex.Replace(input, @"[^a-zA-Z0-9\u4e00-\u9fa5#-]", "");
            return input.Replace("#", "_")
                        .Replace(" ", "")
                        .Replace("\"", "")
                        .Replace("《", "")
                        .Replace("》", "");
        }

        private void btnView_Click(object sender, EventArgs e)
        {
            using (var folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description = "选择下载保存路径";
                //folderDialog.SelectedPath = _downloadPath;

                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    downloadPath = folderDialog.SelectedPath;
                    txtPath.Text = downloadPath;

                    // 更新下载器的下载路径
                    //downloader = new Downloader(downloadPath);
                    //hlsDownloader = new HlsDownloader(downloadPath);

                    downloader.DownloadPath = downloadPath;
                    hlsDownloader.DownloadPath = downloadPath;

                    //downloader.ProgressChanged += Downloader_ProgressChanged;
                    //downloader.DownloadComplete += Downloader_DownloadComplete;
                    //hlsDownloader.ProgressChanged += Downloader_ProgressChanged;
                    //hlsDownloader.DownloadComplete += Downloader_DownloadComplete;
                    //hlsDownloader.LogMessage += HlsDownloader_LogMessage;

                    AddLog($"下载路径已设置为: {downloadPath}");
                }
            }
        }
    }
}
