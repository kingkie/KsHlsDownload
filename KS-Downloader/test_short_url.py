import asyncio
from pathlib import Path
from sys import path

project_root = Path(__file__).parent
path.insert(0, str(project_root))

from rich.console import Console
from httpx import AsyncClient
from source.link.examiner import Examiner


async def test_short_url():
    console = Console()
    
    test_url = "https://v.kuaishou.com/Kxmds319"
    console.print(f"[bold green]测试短链接解析: {test_url}[/bold green]")
    
    class MockManager:
        def __init__(self):
            self.client = AsyncClient(follow_redirects=True, verify=False)
            self.cookie = None
            self.pc_headers = {
                "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8",
                "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
            }
            self.pc_data_headers = self.pc_headers.copy()
            self.console = console
            self.max_retry = 3
    
    manager = MockManager()
    examiner = Examiner(manager)
    
    try:
        result = await examiner.run(test_url, type_="detail")
        console.print(f"[bold green]解析成功! 结果: {result}[/bold green]")
    except Exception as e:
        console.print(f"[bold red]解析失败: {e}[/bold red]")
    finally:
        await manager.client.aclose()


if __name__ == "__main__":
    asyncio.run(test_short_url())