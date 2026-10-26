"""Run with Python 3.10+: python examples/python_report.py"""
from pathlib import Path
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "sdk"))
from progress_island import Task

with Task("Python · 数据处理示例") as task:
    for i in range(40):
        time.sleep(0.2)
        task.update(i + 1, total=40, message=f"处理样本 {i + 1}/40")
