import sys
import asyncio
import os
from pathlib import Path
from PyQt5.QtWidgets import (
    QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
    QLineEdit, QPushButton, QCheckBox, QTextEdit, QProgressBar,
    QLabel, QGroupBox, QMessageBox, QFileDialog, QListWidget,
    QListWidgetItem, QMenu
)
from PyQt5.QtCore import Qt, QThread, pyqtSignal, pyqtSlot, QTimer
from PyQt5.QtGui import QIcon

# 导入 KS 下载器
from source import KS
from source.config import Config
from source.tools import Cleaner, ColorConsole
from source.module import Database

class DownloadThread(QThread):
    """下载线程"""
    progress = pyqtSignal(int, str)  # 进度，消息
    finished = pyqtSignal(bool, tuple)  # 是否成功，(消息，文件路径)
    log_message = pyqtSignal(str)  # 日志消息
    
    def __init__(self, url, save_dir="", parent=None):
        super().__init__(parent)
        self.url = url
        self.save_dir = save_dir  # 保存目录
        self.is_running = True
        self.downloaded_bytes = 0
    
    def run(self):
        try:
            asyncio.run(self.download())
        except Exception as e:
            self.log_message.emit(f"L37,错误：{str(e)}")
            self.finished.emit(False, (str(e), None))
    
    async def download(self):
        self.progress.emit(0, "1初始化下载器...")
        self.log_message.emit("2初始化下载器...")
        
        async with KS() as app:
            # 设置保存目录
            if self.save_dir:
                app.params.work_path = self.save_dir
                self.log_message.emit(f"5保存目录已设置：{self.save_dir}")
            else:
                app.params.work_path = os.path.join("Volume", "Download")
            
            self.progress.emit(10, "3解析链接...")
            self.log_message.emit("4解析链接...")
            
            # 获取下载结果
            result = await app.detail(self.url, download=True)
            
            # 模拟下载进度反馈
            if result is None or isinstance(result, dict):
                self.progress.emit(20, "获取视频信息...")
                self.log_message.emit("获取视频信息...")
                
                self.progress.emit(30, "准备下载...")
                self.log_message.emit("准备下载...")
                
                # 模拟下载进度
                for i in range(30, 101, 10):
                    import time
                    time.sleep(0.2)  # 模拟下载延迟
                    self.progress.emit(i, f"下载中 {i}%")
                    self.log_message.emit(f"下载进度: {i}%")
            
            print("下载结果:")
            print(result)
            if result is None:
                self.progress.emit(100, "下载完成")
                self.log_message.emit("下载完成")
                self.finished.emit(True, ("下载成功！", None))
            elif isinstance(result, dict):
                # 成功获取数据，尝试获取文件路径
                file_path = self.extract_file_path(result)
                self.progress.emit(100, "下载完成")
                self.log_message.emit("下载完成")
                self.finished.emit(True, ("下载成功！", file_path))
            else:
                self.log_message.emit(f"L81,错误：{str(result)}")
                self.finished.emit(False, (str(result), None))
    
    def extract_file_path(self, data: dict) -> str:
        """从下载数据中提取文件路径"""
        try:
            # 尝试从视频数据中获取路径
            if "video" in data:
                video_data = data["video"]
                if isinstance(video_data, list) and len(video_data) > 0:
                    # 获取第一个视频文件的路径
                    return video_data[0].get("path", "")
            # 尝试从下载路径获取
            if "path" in data:
                return data["path"]
        except Exception:
            pass
        return ""
    
    def stop(self):
        self.is_running = False
        self.terminate()

class MainUI(QMainWindow):
    def __init__(self):
        super().__init__()
        self.init_ui()
        self.download_thread = None
        self.save_dir = ""  # 保存目录
        self.downloaded_files = {}  # 存储 URL 和文件路径的映射
        
        # 初始化数据库检查下载记录设置
        self.check_record_setting()
        self.setup_context_menu()  # 设置右键菜单
    
    def init_ui(self):
        self.setWindowTitle("快手视频下载工具(By月夜钓鱼)")
        self.setGeometry(100, 100, 900, 650)
        
        # 设置图标
        icon_path = os.path.join(os.path.dirname(__file__), "docs", "KS-Downloader.ico")
        if os.path.exists(icon_path):
            self.setWindowIcon(QIcon(icon_path))
        
        # 中心窗口
        central_widget = QWidget()
        self.setCentralWidget(central_widget)
        layout = QVBoxLayout(central_widget)
        
        # 标题
        title_label = QLabel("<h1>快手视频下载工具</h1>")
        title_label.setAlignment(Qt.AlignCenter)
        layout.addWidget(title_label)
        
        # URL 输入区域
        url_group = QGroupBox("下载链接")
        url_layout = QHBoxLayout(url_group)
        
        self.url_input = QLineEdit()
        self.url_input.setPlaceholderText("请输入快手作品链接...")
        self.url_input.setFixedHeight(40)  # 增加输入框高度
        self.url_input.setStyleSheet("font-size: 14px; padding: 5px;")
        url_layout.addWidget(self.url_input)
        
        self.download_btn = QPushButton("开始下载")
        self.download_btn.clicked.connect(self.start_download)
        self.download_btn.setFixedHeight(40)  # 增加按钮高度
        self.download_btn.setStyleSheet("font-size: 14px; padding: 5px 20px;")
        url_layout.addWidget(self.download_btn)
        
        layout.addWidget(url_group)
        
        # 设置区域
        settings_group = QGroupBox("设置")
        settings_layout = QHBoxLayout(settings_group)
        
        self.record_checkbox = QCheckBox("启用下载记录")
        self.record_checkbox.stateChanged.connect(self.toggle_record)
        settings_layout.addWidget(self.record_checkbox)
        
        # 保存目录设置
        self.save_dir_btn = QPushButton("选择保存目录")
        self.save_dir_btn.clicked.connect(self.select_save_dir)
        self.save_dir_btn.setFixedHeight(40)
        self.save_dir_btn.setStyleSheet("font-size: 14px; padding: 5px;")
        settings_layout.addWidget(self.save_dir_btn)
        
        self.save_dir_label = QLabel("保存目录：默认")
        settings_layout.addWidget(self.save_dir_label)
        
        # 打开下载目录按钮
        self.open_save_dir_btn = QPushButton("打开下载目录")
        self.open_save_dir_btn.clicked.connect(self.open_save_directory)
        self.open_save_dir_btn.setFixedHeight(40)
        self.open_save_dir_btn.setStyleSheet("font-size: 14px; padding: 5px;")
        settings_layout.addWidget(self.open_save_dir_btn)
        
        layout.addWidget(settings_group)
        
        # 进度条
        self.progress_bar = QProgressBar()
        self.progress_bar.setValue(0)
        layout.addWidget(self.progress_bar)
        
        # 状态标签
        self.status_label = QLabel("就绪")
        self.status_label.setAlignment(Qt.AlignCenter)
        self.status_label.setStyleSheet("color: blue; font-weight: bold;")
        layout.addWidget(self.status_label)
        
        # 下载列表区域
        list_group = QGroupBox("下载列表")
        list_layout = QVBoxLayout(list_group)
        
        self.download_list = QListWidget()
        list_layout.addWidget(self.download_list)
        
        layout.addWidget(list_group)
        
        # 日志输出区域
        log_group = QGroupBox("日志")
        log_layout = QVBoxLayout(log_group)
        
        self.log_text = QTextEdit()
        self.log_text.setReadOnly(True)
        self.log_text.setStyleSheet("font-family: Consolas; font-size: 10pt;")
        log_layout.addWidget(self.log_text)
        
        layout.addWidget(log_group)
        
        # 底部按钮
        bottom_layout = QHBoxLayout()
        
        self.clear_log_btn = QPushButton("清空日志")
        self.clear_log_btn.clicked.connect(self.clear_log)
        self.clear_log_btn.setFixedHeight(40)
        self.clear_log_btn.setStyleSheet("font-size: 14px; padding: 5px;")
        bottom_layout.addWidget(self.clear_log_btn)
        
        self.clear_list_btn = QPushButton("清空列表")
        self.clear_list_btn.clicked.connect(self.clear_list)
        self.clear_list_btn.setFixedHeight(40)
        self.clear_list_btn.setStyleSheet("font-size: 14px; padding: 5px;")
        bottom_layout.addWidget(self.clear_list_btn)
        
        self.quit_btn = QPushButton("退出")
        self.quit_btn.clicked.connect(self.close)
        self.quit_btn.setFixedHeight(40)
        self.quit_btn.setStyleSheet("font-size: 14px; padding: 5px;")
        bottom_layout.addWidget(self.quit_btn)
        
        layout.addLayout(bottom_layout)
    
    def log(self, message):
        """添加日志"""
        from datetime import datetime
        timestamp = datetime.now().strftime("%H:%M:%S")
        self.log_text.append(f"[{timestamp}] {message}")
        # 自动滚动到底部
        self.log_text.verticalScrollBar().setValue(
            self.log_text.verticalScrollBar().maximum()
        )
    
    def clear_log(self):
        """清空日志"""
        self.log_text.clear()
        self.log("日志已清空")
    
    def clear_list(self):
        """清空下载列表"""
        self.download_list.clear()
        self.log("下载列表已清空")
    
    def select_save_dir(self):
        """选择保存目录"""
        dir_path = QFileDialog.getExistingDirectory(self, "选择保存目录")
        if dir_path:
            old_save_dir = self.save_dir
            self.save_dir = dir_path
            self.save_dir_label.setText(f"保存目录：{dir_path}")
            self.log(f"保存目录已设置：{dir_path}")
            
            # 如果之前有下载的文件还在缓存位置，移动到新目录
            if old_save_dir != dir_path and self.downloaded_files:
                self.move_cached_files_to_new_dir()
    
    def open_save_directory(self):
        """打开下载目录"""
        import subprocess
        import os
        
        # 使用默认路径
        default_path = os.path.join(os.path.dirname(__file__), "Downloads")
        dir_path = self.save_dir if self.save_dir else default_path
        
        # 确保路径使用反斜杠（Windows 格式）
        dir_path = os.path.normpath(dir_path)
        
        # 如果目录不存在，尝试创建
        if not Path(dir_path).exists():
            try:
                os.makedirs(dir_path, exist_ok=True)
                self.log(f"已创建下载目录：{dir_path}")
            except Exception as e:
                QMessageBox.warning(self, "警告", f"无法创建下载目录：{e}")
                return
        
        try:
            # 使用 explorer 打开目录
            subprocess.run(['explorer', dir_path], check=True)
            self.log(f"已打开下载目录：{dir_path}")
        except Exception as e:
            # self.log(f"打开下载目录失败：{e}")
            print(e)
    
    def move_cached_files_to_new_dir(self):
        """将缓存中的文件移动到新设置的保存目录"""
        import shutil
        import os
        
        moved_count = 0
        for url, file_path in list(self.downloaded_files.items()):
            try:
                source_path = Path(file_path)
                if not source_path.exists():
                    continue
                
                # 检查文件是否已经在保存目录中
                if str(source_path.parent) == self.save_dir:
                    continue
                
                # 获取文件名
                file_name = source_path.name
                
                # 构建目标路径
                target_dir = Path(self.save_dir)
                if not target_dir.exists():
                    target_dir.mkdir(parents=True, exist_ok=True)
                
                target_path = target_dir / file_name
                
                # 如果目标文件已存在，添加序号
                counter = 1
                while target_path.exists():
                    name, ext = os.path.splitext(file_name)
                    target_path = target_dir / f"{name}_{counter}{ext}"
                    counter += 1
                
                # 移动文件
                shutil.move(str(source_path), str(target_path))
                self.log(f"已移动文件：{file_path} -> {target_path}")
                
                # 更新下载记录中的路径
                self.downloaded_files[url] = str(target_path)
                moved_count += 1
                
            except Exception as e:
                self.log(f"移动文件失败：{file_path}, 错误：{e}")
        
        if moved_count > 0:
            self.log(f"共移动 {moved_count} 个文件到新目录")
    
    def check_record_setting(self):
        """检查下载记录设置"""
        try:
            console = ColorConsole(False)
            config_obj = Config(console)
            config_data = config_obj.read()
            # 使用正确的参数名 data_record
            self.record_checkbox.setChecked(bool(config_data.get("data_record", False)))
        except Exception as e:
            self.log(f"检查设置失败：{e}")
            self.record_checkbox.setChecked(True)
    
    def toggle_record(self, state):
        """切换下载记录设置"""
        try:
            console = ColorConsole(False)
            config_obj = Config(console)
            value = 1 if state == Qt.Checked else 0
            config_data = config_obj.read()
            config_data["data_record"] = bool(value)  # 使用正确的参数名 data_record
            config_obj.write(config_data)
            self.log(f"下载记录已{'启用' if value else '禁用'}")
        except Exception as e:
            self.log(f"修改设置失败：{e}")
    
    def add_to_list(self, url, status, file_path=None):
        """添加到下载列表"""
        item = QListWidgetItem(f"{status}: {url}")
        if status == "成功":
            item.setForeground(Qt.green)
            # 存储文件路径
            if file_path:
                self.downloaded_files[url] = file_path
        else:
            item.setForeground(Qt.red)
        self.download_list.addItem(item)
    
    def setup_context_menu(self):
        """设置右键菜单和双击事件"""
        self.download_list.setContextMenuPolicy(Qt.CustomContextMenu)
        self.download_list.customContextMenuRequested.connect(self.show_context_menu)
        # 添加双击事件处理
        self.download_list.itemDoubleClicked.connect(self.on_item_double_clicked)
    
    def show_context_menu(self, pos):
        """显示右键菜单"""
        item = self.download_list.itemAt(pos)
        if not item:
            return
        
        # 获取 URL
        text = item.text()
        url = None
        for stored_url in self.downloaded_files.keys():
            if stored_url in text:
                url = stored_url
                break
        
        if not url or url not in self.downloaded_files:
            return
        
        # 创建菜单
        menu = QMenu(self)
        open_folder_action = menu.addAction("打开文件夹位置")
        
        # 显示菜单
        action = menu.exec_(self.download_list.mapToGlobal(pos))
        
        # 处理动作
        if action == open_folder_action:
            self.open_file_location(url)
    
    def on_item_double_clicked(self, item):
        """双击列表项时打开文件位置"""
        # 获取 URL
        text = item.text()
        url = None
        for stored_url in self.downloaded_files.keys():
            if stored_url in text:
                url = stored_url
                break
        
        if url and url in self.downloaded_files:
            self.open_file_location(url)
    
    def open_file_location(self, url):
        """打开文件位置并选中文件"""
        import subprocess
        file_path = self.downloaded_files.get(url)
        if not file_path:
            QMessageBox.warning(self, "警告", "未找到下载文件！")
            return
        
        if not Path(file_path).exists():
            QMessageBox.warning(self, "警告", "文件不存在！")
            return
        
        # 使用 explorer /select 选中文件
        try:
            subprocess.run(['explorer', '/select,', str(file_path)], check=True)
            self.log(f"已打开文件位置：{file_path}")
        except Exception as e:
            QMessageBox.critical(self, "错误", f"打开文件夹失败：{e}")
            self.log(f"打开文件夹失败：{e}")
    
    def start_download(self):
        """开始下载"""
        url = self.url_input.text().strip()
        
        if not url:
            QMessageBox.warning(self, "警告", "请输入下载链接！")
            return
        
        if self.download_thread and self.download_thread.isRunning():
            QMessageBox.warning(self, "警告", "下载中，请等待完成！")
            return
        
        # 禁用按钮
        self.download_btn.setEnabled(False)
        self.progress_bar.setValue(0)
        self.status_label.setText("下载中...")
        self.status_label.setStyleSheet("color: orange; font-weight: bold;")
        
        # 启动下载线程（传递保存目录）
        self.download_thread = DownloadThread(url, self.save_dir)
        self.download_thread.progress.connect(self.update_progress)
        self.download_thread.finished.connect(self.download_finished)
        self.download_thread.log_message.connect(self.log)
        self.download_thread.start()
        
        self.log(f"开始下载：{url}")
    
    @pyqtSlot(int, str)
    def update_progress(self, value, message):
        """更新进度"""
        self.progress_bar.setValue(value)
        self.status_label.setText(message)
    
    @pyqtSlot(bool, tuple)
    def download_finished(self, success, result_tuple):
        """下载完成"""
        message, file_path = result_tuple
        self.download_btn.setEnabled(True)
        
        if success:
            self.status_label.setText("下载完成")
            self.status_label.setStyleSheet("color: green; font-weight: bold;")
            self.progress_bar.setValue(100)
            
            # 如果设置了保存目录，将文件移动到保存目录
            final_path = file_path
            if self.save_dir and file_path:
                final_path = self.move_to_save_dir(file_path)
            
            self.add_to_list(self.url_input.text(), "成功", final_path)
            # 下载成功只在日志显示，不弹出对话框
            self.log(message)
            if final_path != file_path:
                self.log(f"文件已移动到保存目录：{final_path}")
        else:
            self.status_label.setText("下载失败")
            self.status_label.setStyleSheet("color: red; font-weight: bold;")
            self.add_to_list(self.url_input.text(), "失败")
            QMessageBox.critical(self, "L505失败", message)
            self.log(message)
    
    def move_to_save_dir(self, file_path):
        """将文件移动到保存目录"""
        import shutil
        import os
        
        try:
            source_path = Path(file_path)
            if not source_path.exists():
                self.log(f"源文件不存在：{file_path}")
                return file_path
            
            # 获取文件名
            file_name = source_path.name
            
            # 构建目标路径
            target_dir = Path(self.save_dir)
            if not target_dir.exists():
                target_dir.mkdir(parents=True, exist_ok=True)
            
            target_path = target_dir / file_name
            
            # 如果目标文件已存在，添加序号
            counter = 1
            while target_path.exists():
                name, ext = os.path.splitext(file_name)
                target_path = target_dir / f"{name}_{counter}{ext}"
                counter += 1
            
            # 移动文件
            shutil.move(str(source_path), str(target_path))
            self.log(f"文件已从 {file_path} 移动到 {target_path}")
            return str(target_path)
        
        except Exception as e:
            self.log(f"移动文件失败：{e}")
            return file_path
    
    def closeEvent(self, event):
        """关闭窗口"""
        if self.download_thread and self.download_thread.isRunning():
            reply = QMessageBox.question(
                self, "确认退出",
                "下载正在进行中，确定要退出吗？",
                QMessageBox.Yes | QMessageBox.No,
                QMessageBox.No
            )
            if reply == QMessageBox.Yes:
                self.download_thread.stop()
                event.accept()
            else:
                event.ignore()
        else:
            event.accept()

def main():
    app = QApplication(sys.argv)
    window = MainUI()
    window.show()
    sys.exit(app.exec_())

if __name__ == "__main__":
    main()