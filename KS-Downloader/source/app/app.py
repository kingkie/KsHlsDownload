from datetime import datetime
from uvicorn import Config as APIConfig
from uvicorn import Server
from ..config import Config, Parameter
from ..downloader import Downloader
from ..extract import APIExtractor, HTMLExtractor
from ..link import DetailPage, Examiner
from ..manager import Manager
from ..module import Database, choose
from ..record import RecordManager
from ..request import Detail, User
from ..static import (
    DISCLAIMER_TEXT,
    LICENCE,
    PROJECT_NAME,
    REPOSITORY,
    VERSION_BETA,
    VERSION_MAJOR,
    VERSION_MINOR,
    __VERSION__,
)
from textwrap import dedent

try:
    from playwright.async_api import async_playwright
    HAS_PLAYWRIGHT = True
except ImportError:
    HAS_PLAYWRIGHT = False
from ..tools import (
    ERROR,
    INFO,
    MASTER,
    WARNING,
    # BrowserCookie,
    Cleaner,
    ColorConsole,
    Mapping,
    Version,
)
from ..translation import _, switch_language
from fastapi import FastAPI
from fastapi.responses import RedirectResponse
from ..model import (
    DetailModel,
    ResponseModel,
    ShortUrl,
    UrlResponse,
)


class KS:
    VERSION_MAJOR = VERSION_MAJOR
    VERSION_MINOR = VERSION_MINOR
    VERSION_BETA = VERSION_BETA

    cleaner = Cleaner()

    NAME = PROJECT_NAME
    WIDTH = 50
    LINE = ">" * WIDTH

    DOMAINS: list[str] = [
        # "chenzhongtech.com",
        "kuaishou.com",
        # "kuaishou.cn",
    ]

    def __init__(
        self,
        server_mode: bool = False,
    ):
        self.console = ColorConsole(
            self.VERSION_BETA,
        )
        self.config_obj = Config(self.console)
        self.params = Parameter(
            console=self.console,
            cleaner=self.cleaner,
            **self.config_obj.read(),
        )
        self.config: dict | None = None
        self.option: dict | None = None
        self.record = RecordManager()
        self.manager = Manager(**self.params.run())
        self.database = Database(self.manager)
        self.mapping = Mapping(self.manager, self.database)
        self.version = Version(self.manager)
        self.examiner = Examiner(self.manager)
        self.detail_html = DetailPage(self.manager)
        self.extractor_api = APIExtractor(self.manager)
        self.extractor_html = HTMLExtractor(self.manager)
        self.download = Downloader(
            self.manager,
            self.database,
            server_mode,
        )
        self.running = True
        self.__function = None

    async def run(self):
        self.config = await self.database.read_config()
        self.option = await self.database.read_option()
        self.set_language(self.option["Language"])
        self.__welcome()
        if await self.disclaimer():
            await self.__main_menu()

    async def __user_enquire(self):
        while self.running:
            text = self.console.input(_("请输入快手账号链接："))
            if not text:
                break
            if text.upper() == "Q":
                self.running = False
                break
            await self.user(text)

    async def __detail_enquire(self):
        while self.running:
            text = self.console.input(_("请输入快手作品链接："))
            if not text:
                break
            if text.upper() == "Q":
                self.running = False
                break
            await self.detail(text)

    # async def __read_cookie(self):
    #     if c := BrowserCookie.run(
    #         self.DOMAINS,
    #         self.console,
    #     ):
    #         self.config_obj.write(self.config_obj.read() | {"cookie": c})
    #         self.console.print(_("读取并写入 Cookie 成功！"), style=INFO)

    async def __main_menu(self):
        while self.running:
            self.__update_menu()
            function = choose(
                _("请选择 KS-Downloader 功能"),
                [i for i, __ in self.__function],
                self.console,
            )
            if function.upper() == "Q":
                self.running = False
            try:
                n = int(function) - 1
            except ValueError:
                break
            if n in range(len(self.__function)):
                await self.__function[n][1]()

    def __update_menu(self):
        tip = {
            0: _("启用"),
            1: _("禁用"),
        }
        self.__function = (
            # (_("从浏览器读取 Cookie"), self.__read_cookie),
            # (_("批量下载账号作品"), self.__user_enquire),
            (_("批量下载链接作品"), self.__detail_enquire),
            (
                tip[self.config["Record"]] + _("下载记录功能"),
                self.__modify_record,
            ),
            (_("检查程序版本更新"), self.__update_version),
            (_("切换语言"), self._switch_language),
        )

    async def _switch_language(
        self,
    ):
        if self.option["Language"] == "zh_CN":
            language = "en_US"
        elif self.option["Language"] == "en_US":
            language = "zh_CN"
        else:
            raise TypeError(self.option["Language"])
        await self._update_language(language)

    async def __update_version(self):
        if target := await self.version.get_target_version():
            state = self.version.compare_versions(
                f"{self.VERSION_MAJOR}.{self.VERSION_MINOR}",
                target,
                self.VERSION_BETA,
            )
            self.console.print(
                self.version.STATUS_CODE[state], style=INFO if state == 1 else WARNING
            )
        else:
            self.console.print(_("检测新版本失败"), style=ERROR)

    async def __modify_record(self):
        await self.__update_config("Record", 0 if self.config["Record"] else 1)
        self.database.record = self.config["Record"]
        self.console.print(
            _("修改设置成功！"),
            style=INFO,
        )

    async def __update_config(self, key: str, value: int):
        self.config[key] = value
        await self.database.update_config_data(key, value)

    def __welcome(self):
        self.console.print(self.LINE, style=MASTER)
        self.console.print("\n")
        self.console.print(self.NAME.center(self.WIDTH), style=MASTER)
        self.console.print("\n")
        self.console.print(self.LINE, style=MASTER)
        self.console.print()
        self.console.print(_("项目地址：{repo}").format(repo=REPOSITORY), style=MASTER)
        self.console.print(
            _("开源协议：{licence}").format(licence=LICENCE), style=MASTER
        )
        self.console.print()
    # 处理作品链接
    # 1. 提取作品链接
    # 2. 处理每个链接
    # 3. 下载视频
    # 4. 更新作者昵称
    # 5. detail 下载具体链接
    async def detail(
        self,
        detail: str,
        download: bool = True,
    ) -> None:
        urls = await self.examiner.run(
            detail,
        )
        print("提取到的链接:",urls)
        if not urls:
            message = _("提取作品链接失败")
            self.console.warning(message)
            return message
        for url in urls:
            if isinstance(
                m := await self.detail_one(
                    url,
                    download,
                ),
                str,
            ):
                self.console.warning(m)
        return None

    async def detail_one(
        self,
        url: str,
        download: bool = False,
        proxy: str = "",
        cookie: str = "",
    ) -> dict | str:
        web, user_id, detail_id = self.examiner.extract_params(
            url,
        )
        if not detail_id:
            message = _("URL 解析失败：{url}").format(url=url)
            self.console.warning(message)
            return message
        data = await self.__handle_detail_html(
            detail_id,
            url,
            web,
            proxy,
            cookie,
        )

        # 处理作品数据
        if not data:
            self.console.warning(_("获取作品数据失败，尝试使用浏览器模式下载..."))
            if download and HAS_PLAYWRIGHT:
                # 尝试使用浏览器模式下载
                #browser_result = await self.__handle_browser_download(url, detail_id)
                #if browser_result and isinstance(browser_result, dict):
                    #return browser_result
                # 浏览器模式失败，尝试HLS下载
                self.console.warning(_("试使用HLS流媒体下载..."))
                hls_result = await self.__handle_hls_download(url, detail_id)
                if hls_result and isinstance(hls_result, dict):
                    return hls_result
                return _("获取作品数据失败")
            return _("获取作品数据失败")
        await self.update_author_nickname(
            data,
        )
        
        if download:
            # 检查 download 链接是否有效
            download_links = data.get("download", [])
            # 清理 download_links，去除可能的引号和空格
            if isinstance(download_links, list):
                cleaned_links = []
                for url in download_links:
                    if isinstance(url, str):
                        # 去除可能的反引号、引号和空格
                        cleaned_url = url.strip().strip('`').strip("'").strip('"')
                        if cleaned_url:
                            cleaned_links.append(cleaned_url)
                download_links = cleaned_links
                data["download"] = download_links
            elif isinstance(download_links, str):
                # 去除可能的反引号、引号和空格
                cleaned_url = download_links.strip().strip('`').strip("'").strip('"')
                data["download"] = cleaned_url
                download_links = [cleaned_url] if cleaned_url else []
            
            # URL去重：基于文件名（去除域名和参数）进行去重
            if download_links and len(download_links) > 0:
                unique_file_names = set()
                deduplicated_links = []
                for url in download_links:
                    if str(url).strip() and str(url).startswith("http"):
                        file_name = self._extract_file_name_from_url(url)
                        if file_name and file_name not in unique_file_names:
                            unique_file_names.add(file_name)
                            deduplicated_links.append(url)
                
                if len(deduplicated_links) < len(download_links):
                    self.console.warning(_("去重前: {count} 个链接, 去重后: {dedup} 个链接").format(
                        count=len(download_links), dedup=len(deduplicated_links)
                    ))
                
                download_links = deduplicated_links
                data["download"] = download_links
            
            has_valid_link = False
            if download_links and len(download_links) > 0:
                for url in download_links:
                    if str(url).strip() and str(url).startswith("http"):
                        has_valid_link = True
                        break
            
            if not has_valid_link:
                self.console.warning(_("download 链接为空或无效，尝试使用HLS流媒体下载..."))
                hls_result = await self.__handle_hls_download(url, detail_id)
                if hls_result and isinstance(hls_result, dict):
                    await self.__save_data([hls_result], "Download")
                    return hls_result
                self.console.error(_("无法获取视频的任何下载链接！"))
                self.console.error(_("可能的原因："))
                self.console.error(_("  1. 该视频已被删除或下架"))
                self.console.error(_("  2. 该视频需要登录或特定权限才能下载"))
                self.console.error(_("  3. 该视频的下载方式不被当前版本支持"))
                return _("下载失败：无法获取有效的下载链接")
            
            # 尝试普通下载，如果失败则尝试HLS
            try:
                await self.__download_file(
                    [data],
                )
            except Exception as e:
                self.console.warning(_("普通下载失败: {error}，尝试使用HLS流媒体下载...").format(error=str(e)))
                hls_result = await self.__handle_hls_download(url, detail_id)
                if hls_result and isinstance(hls_result, dict):
                    await self.__save_data([hls_result], "Download")
                    return hls_result
                self.console.error(_("所有下载方式都失败了！"))
                self.console.error(_("可能的原因："))
                self.console.error(_("  1. 网络连接问题"))
                self.console.error(_("  2. 该视频需要特定的权限或登录"))
                self.console.error(_("  3. 服务器限制了该视频的下载"))
                self.console.error(_("  4. 该视频的下载方式已更新"))
                raise
        
        await self.__save_data([data], "Download")
        return data
    
    async def __handle_browser_download(self, url: str, detail_id: str) -> dict | str:
        """使用浏览器模式下载视频（使用浏览器的下载功能）"""
        try:
            from playwright.async_api import async_playwright
            import re
            import os
            
            download_dir = str(self.manager.temp)

            # 创建临时下载目录
            if self.params.work_path:
                save_dir = str(self.params.work_path)
            else:
                save_dir = str(self.manager.temp)
            
            # 启动浏览器并设置下载路径
            playwright = await async_playwright().start()
            browser = await playwright.chromium.launch(
                headless=True,
                args=[
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-dev-shm-usage",
                    "--disable-gpu",
                    "--disable-extensions",
                    "--disable-blink-features=AutomationControlled",
                    "--mute-audio",
                ],
            )
            
            # 设置浏览器上下文，配置下载（使用手机UA模拟小程序环境）
            context = await browser.new_context(
                user_agent="Mozilla/5.0 (Linux; Android 10; SM-G970F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/92.0.4515.131 Mobile Safari/537.36",
                viewport={"width": 412, "height": 915},
                ignore_https_errors=True,
                accept_downloads=True,
            )
            
            page = await context.new_page()
            
            await page.set_extra_http_headers({
                "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
                "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8",
                "Referer": "https://servicewechat.com/",
            })
            
            # 收集视频URL
            video_urls = []
            downloaded_file = None
            
            # 监听下载事件
            async def handle_download(download):
                nonlocal downloaded_file
                self.console.print(_("开始下载: {url}").format(url=download.url[:100]), style=INFO)
                await download.save_as(os.path.join(save_dir, download.suggested_filename))
                downloaded_file = os.path.join(save_dir, download.suggested_filename)
                self.console.print(_("下载完成: {file}").format(file=downloaded_file), style=INFO)
            
            page.on("download", handle_download)
            
            # 监听视频请求
            async def handle_response(response):
                nonlocal video_urls
                req_url = response.url
                if ".mp4" in req_url.lower() and ("video" in req_url.lower() or "upic" in req_url.lower() or "cdn" in req_url.lower()):
                    self.console.print(_("检测到视频请求: {url}").format(url=req_url[:100]), style=INFO)
                    video_urls.append(req_url)
            
            page.on("response", handle_response)
            
            self.console.print(_("正在加载页面: {url}").format(url=url), style=INFO)
            await page.goto(url, wait_until="domcontentloaded", timeout=60000)
            await page.wait_for_timeout(5000)
            
            # 尝试触发视频下载
            video_elements = await page.query_selector_all("video")
            for video in video_elements:
                src = await video.get_attribute("src")
                if src and ".mp4" in src.lower():
                    video_urls.append(src)
                    # 尝试直接下载视频
                    try:
                        self.console.print(_("尝试下载视频: {url}").format(url=src[:100]), style=INFO)
                        await page.evaluate(f'''(url) => {{
                            const link = document.createElement('a');
                            link.href = url;
                            link.download = '{detail_id}.mp4';
                            document.body.appendChild(link);
                            link.click();
                            document.body.removeChild(link);
                        }}''', src)
                        await page.wait_for_timeout(10000)
                    except Exception as e:
                        self.console.print(_("触发下载失败: {error}").format(error=str(e)), style=INFO)
            
            # 如果没有自动下载，尝试直接访问视频URL触发下载
            if not downloaded_file and video_urls:
                video_url = max(video_urls, key=lambda x: len(x))
                self.console.print(_("直接访问视频URL: {url}").format(url=video_url[:100]), style=INFO)
                await page.goto(video_url, wait_until="load", timeout=120000)
                await page.wait_for_timeout(10000)
            
            # 检查下载目录中是否有新文件
            if not downloaded_file:
                files = [f for f in os.listdir(download_dir) if f.endswith('.mp4')]
                if files:
                    downloaded_file = os.path.join(download_dir, files[-1])
                    self.console.print(_("找到下载文件: {file}").format(file=downloaded_file), style=INFO)
            
            await page.close()
            await context.close()
            await browser.close()
            await playwright.stop()
            
            if not downloaded_file or not os.path.exists(downloaded_file):
                return _("未找到下载的视频文件")
            
            # 检查文件大小
            file_size = os.path.getsize(downloaded_file)
            self.console.print(_("下载的视频文件大小: {size} KB").format(size=file_size//1024), style=INFO)
            
            # 如果文件小于5MB，可能是预览视频，尝试使用HLS下载
            if file_size < 5 * 1024 * 1024:
                self.console.warning(_("视频文件较小，可能是预览版本。尝试使用HLS流媒体下载完整视频..."))
                hls_result = await self.__handle_hls_download(url, detail_id)
                if hls_result and isinstance(hls_result, dict):
                    return hls_result
                self.console.warning(_("HLS下载失败，返回浏览器下载的预览视频"))
            
            # 重命名文件
            new_path = os.path.join(save_dir, f"{detail_id}.mp4")
            if downloaded_file != new_path:
                os.rename(downloaded_file, new_path)
                downloaded_file = new_path
            
            self.console.print(_("视频下载完成，大小: {size} MB").format(size=file_size//(1024*1024)), style=INFO)
            self.console.print(_("视频已保存: {path}").format(path=downloaded_file), style=INFO)
            
            # 获取视频URL（用于记录）
            video_url = video_urls[0] if video_urls else ""
            
            # 创建下载数据
            data = {
                "collection_time": datetime.now().strftime("%Y-%m-%d_%H:%M:%S"),
                "photoType": _("视频"),
                "detailID": detail_id,
                "caption": "",
                "coverUrl": "",
                "duration": "00:00:00",
                "realLikeCount": -1,
                "download": [video_url],
                "timestamp": datetime.now().strftime("%Y-%m-%d_%H:%M:%S"),
                "viewCount": -1,
                "shareCount": -1,
                "commentCount": -1,
                "authorID": "unknown",
                "name": "unknown",
                "userSex": "未知",
            }
            
            await self.__save_data([data], "Download")
            return data
            
        except Exception as e:
            self.console.error(_("浏览器模式下载失败: {error}").format(error=str(e)))
            import traceback
            traceback.print_exc()
            return _("浏览器模式下载失败: {error}").format(error=str(e))

    async def __handle_hls_download(self, url: str, detail_id: str) -> dict | str:
        """使用HLS流媒体协议下载完整视频"""
        try:
            import httpx
            import re
            import os
            
            download_dir = os.path.normpath(str(self.manager.temp))

            # 下载目录
            if self.params.work_path:
                save_dir = os.path.normpath(str(self.params.work_path))
            else:
                save_dir = os.path.normpath(str(self.manager.temp))
            
            self.console.print(_("HLS下载到,temp:{temp};save:{save}").format(temp=download_dir, save=save_dir), style=INFO)
            
            # 确保下载目录存在
            if not os.path.exists(download_dir):
                os.makedirs(download_dir, exist_ok=True)
            # 确保保存目录存在
            if not os.path.exists(save_dir):
                os.makedirs(save_dir, exist_ok=True)
            
            headers = {
                "User-Agent": "Mozilla/5.0 (Linux; Android 10; SM-G970F) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/92.0.4515.131 Mobile Safari/537.36",
                "Referer": "https://v.m.chenzhongtech.com/",
            }
            
            async with httpx.AsyncClient(timeout=300, follow_redirects=True) as client:
                # 1. 获取页面内容，查找m3u8 URL
                self.console.print(_("HLS下载: 获取页面内容..."), style=INFO)
                response = await client.get(url, headers=headers)
                content = response.text
                
                # 搜索m3u8 URL
                m3u8_url = None
                
                # 模式1: 直接搜索.m3u8 URL
                match = re.search(r'https?://[^\s,"\'<>]+\.m3u8[^\s,"\'<>]*', content)
                if match:
                    m3u8_url = match.group(0)
                    self.console.print(_("HLS下载: 找到m3u8 URL: {url}").format(url=m3u8_url[:100]), style=INFO)
                else:
                    # 模式2: 搜索video-hls模式
                    match = re.search(r'https?://[^\s,"\'<>]+video-hls/[^\s,"\'<>]*\.m3u8', content)
                    if match:
                        m3u8_url = match.group(0)
                        self.console.print(_("HLS下载: 找到video-hls URL: {url}").format(url=m3u8_url[:100]), style=INFO)
                
                if not m3u8_url:
                    return _("HLS下载: 未找到m3u8 URL")
                
                # 2. 获取m3u8文件
                self.console.print(_("HLS下载: 获取m3u8文件..."), style=INFO)
                response = await client.get(m3u8_url, headers=headers)
                m3u8_content = response.text
                
                # 3. 解析m3u8获取所有ts文件
                self.console.print(_("HLS下载: 解析m3u8文件..."), style=INFO)
                ts_urls = []
                base_url = m3u8_url.rsplit('/', 1)[0] + '/'
                
                for line in m3u8_content.split('\n'):
                    line = line.strip()
                    if line and not line.startswith('#'):
                        if line.startswith('http'):
                            ts_urls.append(line)
                        else:
                            ts_urls.append(base_url + line)
                
                if not ts_urls:
                    return _("HLS下载: 未找到ts分片")
                
                self.console.print(_("HLS下载: 找到 {count} 个ts分片").format(count=len(ts_urls)), style=INFO)
                
                # 4. 下载所有ts文件
                self.console.print(_("HLS下载: 开始下载ts分片..."), style=INFO)
                ts_files = []
                
                for i, ts_url in enumerate(ts_urls):
                    self.console.print(_("HLS下载: 下载分片 {current}/{total}").format(current=i+1, total=len(ts_urls)), style=INFO)
                    response = await client.get(ts_url, headers=headers)
                    ts_data = response.content
                    
                    ts_path = os.path.normpath(os.path.join(download_dir, f"{detail_id}.part{i:03d}.ts"))
                    ts_path = ts_path.replace('/', '\\')  # Windows 路径
                    with open(ts_path, "wb") as f:
                        f.write(ts_data)
                    ts_files.append(ts_path)
                
                # 5. 合并ts文件
                self.console.print(_("HLS下载: 合并ts文件..."), style=INFO)
                output_path = os.path.normpath(os.path.join(save_dir, f"{detail_id}_hls.mp4"))
                output_path = output_path.replace('/', '\\')  # Windows 路径
                
                with open(output_path, "wb") as out:
                    for ts_file in ts_files:
                        with open(ts_file, "rb") as f:
                            out.write(f.read())
                        os.remove(ts_file)
                
                # 6. 检查文件大小
                file_size = os.path.getsize(output_path)
                self.console.print(_("HLS下载完成! 文件大小: {size} MB").format(size=file_size // (1024 * 1024)), style=INFO)
                self.console.print(_("HLS视频已保存: {path}").format(path=output_path), style=INFO)
                
                # 创建下载数据
                data = {
                    "collection_time": datetime.now().strftime("%Y-%m-%d_%H:%M:%S"),
                    "photoType": _("视频"),
                    "detailID": detail_id,
                    "caption": "",
                    "coverUrl": "",
                    "duration": "00:00:00",
                    "realLikeCount": -1,
                    "download": [m3u8_url],
                    "timestamp": datetime.now().strftime("%Y-%m-%d_%H:%M:%S"),
                    "viewCount": -1,
                    "shareCount": -1,
                    "commentCount": -1,
                    "authorID": "unknown",
                    "name": "unknown",
                    "userSex": "未知",
                }
                
                await self.__save_data([data], "Download")
                return data
                
        except Exception as e:
            self.console.error(_("HLS下载失败: {error}").format(error=str(e)))
            import traceback
            traceback.print_exc()
            return _("HLS下载失败: {error}").format(error=str(e))

    async def update_author_nickname(
        self,
        data: dict,
    ):
        try:
            print("L604,更新作者昵称:", data)
            i = data.get("authorID", "unknown")
            if a := self.cleaner.filter_name(
                self.manager.mapping_data.get(i, "")
            ):
                print("L609")
                data["name"] = a
            else:
                data["name"] = self.manager.filter_name(data.get("name", "")) or i
                print("L613")
            print("L614")
            await self.mapping.update_cache(
                i,
                data["name"],
            )
        except Exception as e:
            print("L616,更新作者昵称失败:", e)
            self.console.print(_("L614,更新作者昵称失败: {error}").format(error=str(e)))
            import traceback
            traceback.print_exc()

    async def __handle_detail_api(
        self,
        user_id: str,
        detail_id: str,
    ):
        data = await Detail(
            self.manager,
            user_id,
            detail_id,
        ).run()
        data = self.extractor_api.run([data])
        # await self.__save_data(data, "Download")
        return data

    async def __handle_detail_html(
        self,
        detail_id: str,
        url: str,
        web: bool,
        proxy: str = "",
        cookie: str = "",
    ) -> dict | None:
        if html := await self.detail_html.run(url, proxy, cookie):
            return self.extractor_html.run(
                html,
                detail_id,
                web,
            )
        return None

    async def __save_data(
        self, data: list[dict], name: str, type_="detail", format_="SQLite"
    ) -> None:
        recorder, params = self.record.run(type_, format_)
        async with recorder(self.manager, db_name=name, **params) as record:
            for i in data:
                i["download"] = " ".join(i["download"])
                await record.update(i)

    async def __download_file(
        self,
        data: list[dict],
        type_="detail",
    ):
        await self.download.run(
            data,
            type_,
        )

    @staticmethod
    def _extract_file_name_from_url(url: str) -> str:
        """从URL中提取文件名（去除域名和参数）"""
        if not url:
            return ""
        
        try:
            # 清理 URL
            url = url.strip().strip('`').strip("'").strip('"')
            
            # 去除参数部分
            param_index = url.find('?')
            if param_index > 0:
                url = url[:param_index]
            
            # 获取路径最后一部分（文件名）
            from urllib.parse import urlparse
            parsed = urlparse(url)
            file_name = parsed.path.split('/')[-1]
            
            return file_name
        except:
            # 如果解析失败，尝试手动提取
            import re
            match = re.search(r'/([^/]+)$', url)
            if match:
                file_name = match.group(1)
                # 去除参数
                question_mark = file_name.find('?')
                if question_mark > 0:
                    file_name = file_name[:question_mark]
                return file_name
        
        return ""

    @staticmethod
    def _is_video_url(url: str) -> bool:
        """检查URL是否是视频文件（基于扩展名，自动去除参数）"""
        if not url:
            return False
        
        file_name = KS._extract_file_name_from_url(url)
        if not file_name:
            return False
        
        import re
        extension = re.search(r'\.([^.]+)$', file_name, re.IGNORECASE)
        if not extension:
            return False
        
        ext = extension.group(1).lower()
        return ext in ("mp4", "m3u8", "ts", "webm", "mkv", "avi")

    async def user(
        self,
        text: str,
        download: bool = True,
    ) -> None:
        items: list[tuple[str, str]] = await self.examiner.run(
            text,
            "user",
        )
        if not any(items):
            message = _("提取账号链接失败")
            self.console.warning(message)
            return message
        for url, id_ in items:
            if isinstance(
                m := await self.user_one(
                    url,
                    id_,
                    download=download,
                ),
                str,
            ):
                self.console.warning(m)
        return None

    async def user_one(
        self,
        user_url: str,
        user_id: str,
        cursor: str = "",
        download: bool = False,
        proxy: str = "",
        cookie: str = "",
    ):
        response = await User(
            self.manager,
            cookie,
            proxy,
            user_id,
            cursor,
        ).run()
        print(response)

    async def disclaimer(self):
        if self.config["Disclaimer"]:
            return True
        await self.__init_language()
        self.console.print(
            _(DISCLAIMER_TEXT),
            style=MASTER,
        )
        if self.console.input(
            _("是否已仔细阅读上述免责声明(YES/NO): ")
        ).upper() not in (
            "YES",
            "Y",
        ):
            return False
        await self.database.update_config_data("Disclaimer", 1)
        self.console.print()
        return True

    async def __init_language(self):
        languages = (
            (
                "简体中文",
                "zh_CN",
            ),
            (
                "English",
                "en_US",
            ),
        )
        language = choose(
            "请选择语言(Please Select Language)",
            [i[0] for i in languages],
            self.console,
        )
        try:
            language = languages[int(language) - 1][1]
            await self._update_language(language)
        except ValueError:
            await self.__init_language()

    async def _update_language(self, language: str) -> None:
        self.option["Language"] = language
        await self.database.update_option_data("Language", language)
        self.set_language(language)

    async def close(self):
        await self.manager.close()

    async def __aenter__(self):
        await self.database.__aenter__()
        return self

    async def __aexit__(self, exc_type, exc_val, exc_tb):
        await self.database.__aexit__(exc_type, exc_val, exc_tb)
        await self.close()

    @staticmethod
    def set_language(language: str) -> None:
        switch_language(language)

    async def run_api_server(
        self,
        host="0.0.0.0",
        port=5556,
        log_level="info",
    ):
        api = FastAPI(
            debug=self.VERSION_BETA,
            title="KS-Downloader",
            version=__VERSION__,
        )
        self.setup_routes(api)
        config = APIConfig(
            api,
            host=host,
            port=port,
            log_level=log_level,
        )
        server = Server(config)
        await server.serve()

    def setup_routes(
        self,
        server: FastAPI,
    ):
        @server.get(
            "/",
            summary=_("跳转至项目 GitHub 仓库"),
            description=_("重定向至项目 GitHub 仓库主页"),
            tags=["Project"],
        )
        async def index():
            return RedirectResponse(url=REPOSITORY)

        @server.post(
            "/share",
            summary=_("获取作品分享链接的重定向链接"),
            description=_(
                dedent(
                    """
                    **参数**:
                            
                    - **text**: 包含作品链接的文本；必需参数
                    - **proxy**: 请求数据时使用的代理；可选参数
                    """
                )
            ),
            tags=["API"],
            response_model=UrlResponse,
        )
        async def share(extract: ShortUrl):
            if urls := await self.examiner.run(
                extract.text,
                type_="",
                proxy=extract.proxy,
            ):
                return UrlResponse(
                    message=_("请求重定向链接成功！"),
                    params=extract,
                    urls=urls,
                )
            return UrlResponse(
                message=_("请求重定向链接失败！"),
                params=extract,
                urls=None,
            )

        @server.post(
            "/detail/",
            summary=_("获取作品数据"),
            description=_(
                dedent(
                    """
                    **参数**:
                        
                    - **text**: 作品链接，自动提取；必需参数
                    - **cookie**: 请求数据时使用的 Cookie；可选参数
                    - **proxy**: 请求数据时使用的代理；可选参数
                    """
                )
            ),
            tags=["API"],
            response_model=ResponseModel,
        )
        async def detail(extract: DetailModel):
            urls = await self.examiner.run(extract.text, proxy=extract.proxy)
            if not urls:
                message = _("提取作品链接失败")
                data = None
                self.console.warning(message)
            else:
                if isinstance(
                    data := await self.detail_one(
                        urls[0], proxy=extract.proxy, cookie=extract.cookie
                    ),
                    dict,
                ):
                    message = _("获取作品数据成功")
                else:
                    message = data
                    data = None
            return ResponseModel(message=message, params=extract, data=data)
