"""A dependency-free, 60-second task for testing Progress Island."""

import argparse
import time


def main():
    parser = argparse.ArgumentParser(description="任务岛 Python 进度测试")
    parser.add_argument(
        "--seconds", type=float, default=60, help="运行时长，默认 60 秒"
    )
    args = parser.parse_args()
    if not 0 < args.seconds <= 3600:
        parser.error("--seconds 必须大于 0，且不超过 3600")

    steps = 60
    started = time.monotonic()
    checksum = 0
    print("Python 测试开始：island_test.py", flush=True)
    print("进度 0% | 准备处理数据", flush=True)

    try:
        for step in range(1, steps + 1):
            checksum += sum(number * number for number in range(20_000))
            deadline = started + args.seconds * step / steps
            time.sleep(max(0, deadline - time.monotonic()))
            percent = step * 100 // steps
            print(
                f"进度 {percent}% | 已处理 {step}/{steps} 批数据",
                flush=True,
            )
    except KeyboardInterrupt:
        print("测试已手动中止。", flush=True)
        return 130

    print(f"测试完成！校验值：{checksum}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
