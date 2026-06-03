from contextlib import suppress
from typing import TYPE_CHECKING
import os

if TYPE_CHECKING:
    from pathlib import Path


def remove_empty_directories(path: "Path") -> None:
    exclude = {
        "\\.",
        "\\_",
        "\\__",
    }
    # 使用 os.walk 替代 Path.walk 以兼容 Python 3.11 及以下版本
    for root, dirs, files in os.walk(path, topdown=False):
        dir_path = path.joinpath(root) if isinstance(root, str) else root
        if any(i in str(dir_path) for i in exclude):
            continue
        if not dirs and not files:
            with suppress(OSError):
                os.rmdir(dir_path)
