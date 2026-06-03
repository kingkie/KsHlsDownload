from typing import TYPE_CHECKING
from httpx import get
from ..tools import capture_error_request, retry_request, wait
from ..tools.console import INFO, ERROR, WARNING
from ..variable import TIMEOUT

try:
    from playwright.async_api import async_playwright
    HAS_PLAYWRIGHT = True
except ImportError:
    HAS_PLAYWRIGHT = False

if TYPE_CHECKING:
    from ..manager import Manager


class DetailPage:
    def __init__(self, manager: "Manager"):
        self.client = manager.client
        self.headers = manager.pc_headers
        self.console = manager.console
        self.retry = manager.max_retry

    async def run(self, url: str, proxy: str = "", cookie: str = "") -> str:
        result = await self.request_url(url, proxy, cookie)
        if result is None:
            self.console.print("HTTP请求获取页面失败或返回空内容", style=WARNING)
            if HAS_PLAYWRIGHT:
                self.console.print("尝试使用浏览器模式获取页面...", style=INFO)
                return await self.request_url_browser(url)
            return None
        return result

    @retry_request
    @capture_error_request
    async def request_url(
        self,
        url: str,
        proxy: str = "",
        cookie: str = "",
    ) -> str:
        headers = self.headers.copy()
        if cookie:
            headers["Cookie"] = cookie
        if proxy:
            response = get(
                url,
                headers=headers,
                proxy=proxy,
                follow_redirects=True,
                verify=False,
                timeout=TIMEOUT,
            )
        else:
            response = await self.client.get(
                url,
                headers=headers,
            )
        await wait()
        response.raise_for_status()
        return response.text

    async def request_url_browser(self, url: str) -> str:
        try:
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
            context = await browser.new_context(
                user_agent="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                viewport={"width": 1920, "height": 1080},
                ignore_https_errors=True,
            )
            context.set_default_timeout(30000)
            page = await context.new_page()
            
            await page.set_extra_http_headers({
                "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
                "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8",
            })
            
            self.console.print(f"正在加载页面: {url}", style=INFO)
            await page.goto(url, wait_until="domcontentloaded", timeout=30000)
            await page.wait_for_timeout(2000)
            
            content = await page.content()
            
            await page.close()
            await context.close()
            await browser.close()
            await playwright.stop()
            
            self.console.print("浏览器模式获取页面成功", style=INFO)
            return content
        except Exception as e:
            self.console.print(f"浏览器模式获取页面失败: {e}", style=ERROR)
            raise