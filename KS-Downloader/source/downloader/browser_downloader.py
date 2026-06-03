from asyncio import sleep
from pathlib import Path
from typing import TYPE_CHECKING
import re
import json

from playwright.async_api import (
    async_playwright,
    BrowserContext,
    Page,
    Route,
)

from ..tools import capture_error_request, retry_request
from ..tools.console import INFO, ERROR
from ..translation import _

if TYPE_CHECKING:
    from rich.progress import Progress


class BrowserDownloader:
    VIDEO_EXTENSIONS = (".mp4", ".m3u8", ".ts", ".flv", ".webm", ".mov", ".avi")
    VIDEO_PATTERNS = [
        r'https?://[^"\'>\s]+/(?:video|photo|feed|stream)[^"\'>\s]*\.mp4',
        r'"playAddr"\s*:\s*"([^"]+)"',
        r'"src"\s*:\s*"([^"]+\.mp4[^"]*)"',
        r'"url"\s*:\s*"([^"]+\.mp4[^"]*)"',
        r'"videoUrl"\s*:\s*"([^"]+)"',
        r'"mainUrl"\s*:\s*"([^"]+)"',
        r'"data-video-url"\s*:\s*"([^"]+)"',
        r'<video[^>]+src="([^"]+)"',
        r'"playApi"\s*:\s*"([^"]+)"',
        r'"downloadUrl"\s*:\s*"([^"]+)"',
    ]

    def __init__(
        self,
        console,
        temp: Path,
        chunk: int = 1024 * 1024,
        retry: int = 3,
    ):
        self.console = console
        self.temp = temp
        self.chunk = chunk
        self.retry = retry
        self.browser = None
        self.context = None
        self.page = None
        self.video_buffer = b""
        self.video_url = None
        self.download_completed = False

    async def __aenter__(self):
        await self.__init_browser()
        return self

    async def __aexit__(self, exc_type, exc_val, exc_tb):
        await self.__close_browser()

    async def __init_browser(self):
        try:
            playwright = await async_playwright().start()
            self.browser = await playwright.chromium.launch(
                headless=True,
                args=[
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-dev-shm-usage",
                    "--disable-gpu",
                    "--disable-extensions",
                    "--disable-blink-features=AutomationControlled",
                    "--mute-audio",
                    "--disable-web-security",
                ],
            )
            self.context = await self.browser.new_context(
                user_agent="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                viewport={"width": 1920, "height": 1080},
                ignore_https_errors=True,
            )
            self.context.set_default_timeout(60000)
            self.page = await self.context.new_page()
            await self.__setup_request_interception()
            await self.__add_extra_headers()
        except Exception as e:
            self.console.print(_("初始化浏览器失败: {error}").format(error=str(e)), style=ERROR)
            raise

    async def __add_extra_headers(self):
        await self.page.set_extra_http_headers({
            "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
            "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8",
        })

    async def __close_browser(self):
        if self.page:
            try:
                await self.page.close()
            except Exception:
                pass
        if self.context:
            try:
                await self.context.close()
            except Exception:
                pass
        if self.browser:
            try:
                await self.browser.close()
            except Exception:
                pass

    async def __setup_request_interception(self):
        if not self.page:
            return

        async def handle_route(route: Route):
            request = route.request
            url = request.url

            if self.__is_video_request(url):
                self.console.print(_("检测到视频请求: {url}").format(url=url[:120]), style=INFO)
                self.video_url = url
                try:
                    response = await route.fetch()
                    content_type = response.headers.get("content-type", "")
                    if "json" in content_type or "text" in content_type:
                        body = await response.body()
                        video_url = self.__extract_video_url_from_response(body)
                        if video_url:
                            self.console.print(_("从JSON响应中提取到视频URL: {url}").format(url=video_url[:100]), style=INFO)
                            video_response = await self.context.request.get(video_url)
                            self.video_buffer = await video_response.body()
                            self.download_completed = True
                            await route.fulfill(
                                status=200,
                                content_type="video/mp4",
                                body=self.video_buffer,
                            )
                            return
                    self.video_buffer = await response.body()
                    self.download_completed = True
                    await route.fulfill(response=response)
                except Exception as e:
                    self.console.print(_("获取视频数据失败: {error}").format(error=str(e)), style=ERROR)
                    await route.continue_()
            else:
                await route.continue_()

        await self.page.route("**/*", handle_route)

    def __is_video_request(self, url: str) -> bool:
        url_lower = url.lower()
        if any(ext in url_lower for ext in self.VIDEO_EXTENSIONS):
            return True
        if any(pattern in url_lower for pattern in ["video", "photo", "feed", "stream", "play", "download"]):
            return True
        return False

    def __extract_video_url_from_response(self, body: bytes) -> str:
        try:
            text = body.decode("utf-8", errors="ignore")
            for pattern in self.VIDEO_PATTERNS:
                match = re.search(pattern, text)
                if match:
                    return match.group(1)
            data = json.loads(text)
            return self.__extract_video_from_json(data)
        except Exception:
            return ""

    def __extract_video_from_json(self, data, depth=0):
        if depth > 5:
            return ""
        if isinstance(data, dict):
            for key in ["playAddr", "src", "url", "videoUrl", "mainUrl", "playApi", "downloadUrl"]:
                if key in data and isinstance(data[key], str):
                    url = data[key]
                    if any(ext in url.lower() for ext in self.VIDEO_EXTENSIONS):
                        return url
            for value in data.values():
                result = self.__extract_video_from_json(value, depth + 1)
                if result:
                    return result
        elif isinstance(data, list):
            for item in data:
                result = self.__extract_video_from_json(item, depth + 1)
                if result:
                    return result
        return ""

    @retry_request
    @capture_error_request
    async def download_video(
        self,
        url: str,
        path: Path,
        progress: "Progress" = None,
        tip: str = "",
        suffix: str = "mp4",
    ) -> bool:
        try:
            self.video_buffer = b""
            self.video_url = None
            self.download_completed = False

            text = path.name
            self.console.print(
                _("【{type}】{name} 正在使用浏览器模式下载").format(type=tip, name=text),
                style=INFO,
            )

            task_id = None
            if progress:
                task_id = progress.add_task(
                    f"【{tip}】{text}",
                    total=None,
                    completed=0,
                )

            if url.lower().endswith(self.VIDEO_EXTENSIONS):
                await self.__download_direct_video(url, progress, task_id)
            else:
                await self.__download_page_video(url, progress, task_id)

            if not self.video_buffer:
                self.console.print(_("【{type}】{name} 未获取到视频数据，尝试页面分析").format(type=tip, name=text), style=INFO)
                await self.__extract_video_from_page()
                if not self.video_buffer:
                    await self.__try_play_and_capture()

            if not self.video_buffer:
                self.console.print(_("【{type}】{name} 未获取到视频数据").format(type=tip, name=text), style=ERROR)
                return False

            temp = self.temp.joinpath(f"{path.name}.{suffix}")
            with open(temp, "wb") as f:
                f.write(self.video_buffer)

            from .downloader import Downloader

            Downloader.move(temp, path.with_name(f"{path.name}.{suffix}"))

            if progress and task_id:
                progress.update(task_id, completed=len(self.video_buffer))

            self.console.print(
                _("【{type}】{name} 浏览器模式下载完成，大小: {size}")
                .format(type=tip, name=text, size=len(self.video_buffer) // 1024),
                style=INFO,
            )
            return True

        except Exception as e:
            self.console.print(
                _("【{type}】{name} 浏览器模式下载失败: {error}").format(
                    type=tip, name=path.name, error=str(e)
                ),
                style=ERROR,
            )
            return False

    async def __download_direct_video(self, url: str, progress, task_id):
        try:
            response = await self.context.request.get(url)
            self.video_buffer = await response.body()
            self.download_completed = True
            if progress and task_id:
                progress.update(task_id, total=len(self.video_buffer))
        except Exception as e:
            self.console.print(_("直接下载视频失败: {error}").format(error=str(e)), style=ERROR)

    async def __download_page_video(self, url: str, progress, task_id):
        try:
            self.console.print(_("正在加载页面: {url}").format(url=url), style=INFO)
            await self.page.goto(url, wait_until="domcontentloaded", timeout=60000)
            await self.page.wait_for_timeout(3000)
            
            for i in range(15):
                if self.download_completed or self.video_buffer:
                    break
                await sleep(1)
                if progress and task_id and self.video_buffer:
                    progress.update(task_id, completed=len(self.video_buffer))
                if i % 5 == 0:
                    self.console.print(_("等待视频加载... ({i}/15)").format(i=i+1), style=INFO)

            if not self.video_buffer:
                await self.__extract_video_from_page()

        except Exception as e:
            self.console.print(_("页面加载失败: {error}").format(error=str(e)), style=ERROR)

    async def __extract_video_from_page(self):
        try:
            video_elements = await self.page.query_selector_all("video")
            for video in video_elements:
                src = await video.get_attribute("src")
                if src and any(ext in src.lower() for ext in self.VIDEO_EXTENSIONS):
                    self.console.print(_("找到 video 元素视频源: {src}").format(src=src[:100]), style=INFO)
                    await self.__download_direct_video(src, None, None)
                    if self.video_buffer:
                        return

                poster = await video.get_attribute("poster")
                if poster and any(ext in poster.lower() for ext in self.VIDEO_EXTENSIONS):
                    self.console.print(_("找到 video 海报图片: {src}").format(src=poster[:100]), style=INFO)

            page_content = await self.page.content()
            for pattern in self.VIDEO_PATTERNS:
                match = re.search(pattern, page_content)
                if match:
                    video_url = match.group(1)
                    if not video_url.startswith("http"):
                        video_url = "https://" + video_url.lstrip("/")
                    self.console.print(_("从页面中找到视频URL: {url}").format(url=video_url[:100]), style=INFO)
                    await self.__download_direct_video(video_url, None, None)
                    if self.video_buffer:
                        return

            script_tags = await self.page.query_selector_all("script")
            for script in script_tags:
                content = await script.inner_text()
                if content and ("video" in content.lower() or "player" in content.lower()):
                    for pattern in self.VIDEO_PATTERNS:
                        match = re.search(pattern, content)
                        if match:
                            video_url = match.group(1)
                            if not video_url.startswith("http"):
                                video_url = "https://" + video_url.lstrip("/")
                            self.console.print(_("从脚本中找到视频URL: {url}").format(url=video_url[:100]), style=INFO)
                            await self.__download_direct_video(video_url, None, None)
                            if self.video_buffer:
                                return

        except Exception as e:
            self.console.print(_("提取视频失败: {error}").format(error=str(e)), style=ERROR)

    async def __try_play_and_capture(self):
        try:
            self.console.print(_("尝试播放视频并捕获..."), style=INFO)
            await self.page.evaluate("""
                () => {
                    const videos = document.querySelectorAll('video');
                    videos.forEach(v => {
                        if (v.paused) {
                            v.play().catch(() => {});
                        }
                    });
                }
            """)
            await sleep(5)
        except Exception as e:
            self.console.print(_("播放视频失败: {error}").format(error=str(e)), style=ERROR)

    @classmethod
    async def download(
        cls,
        url: str,
        path: Path,
        console,
        temp: Path,
        progress: "Progress" = None,
        tip: str = "",
        suffix: str = "mp4",
    ) -> bool:
        async with cls(console, temp) as downloader:
            return await downloader.download_video(url, path, progress, tip, suffix)