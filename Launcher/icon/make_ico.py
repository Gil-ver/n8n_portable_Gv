# -*- coding: utf-8 -*-
r"""
由 512x512 PNG 生成多尺寸 ICO（供启动器 exe 图标 / 托盘图标使用）。

用法：
    runtime\python\python\python.exe Launcher\icon\make_ico.py

产出（写入 Launcher\n8n_Launcher_Gv\icon\）：
    n8n_launcher_Gv.png  ->  app_icon.ico       (exe 文件图标)
    n8n_launcher.png     ->  n8n_launcher.ico   (托盘图标)
    n8n_launcher.png     ->  n8n_launcher.png   (窗口标题栏图标，原样复制)

每个 ICO 内嵌 16/24/32/48/64/128/256 七种尺寸，
各尺寸均由原图用 LANCZOS 单独高质量缩放，避免系统实时缩放导致小尺寸糊化。

关于 PNG 复制：csproj 里 <Resource Include="icon\n8n_launcher.png" /> 用的是
相对项目目录的路径，编译时读的是 Launcher\n8n_Launcher_Gv\icon\ 下那份，
而设计源文件放在外层 Launcher\icon\，所以必须同步一次，否则改了不生效。
（about.png 走的是 <Resource Include="..\icon\about.png"> + <Link>，直接读外层，无需复制。）
"""
import os
import shutil
import sys

from PIL import Image

# 脚本位于 <root>\Launcher\icon\，向上两级即整合包根目录
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SRC_DIR = os.path.join(ROOT, "Launcher", "icon")
DST_DIR = os.path.join(ROOT, "Launcher", "n8n_Launcher_Gv", "icon")

SIZES = [16, 24, 32, 48, 64, 128, 256]

TASKS = [
    ("n8n_launcher_Gv.png", "app_icon.ico"),      # exe 文件图标
    ("n8n_launcher.png", "n8n_launcher.ico"),     # 托盘图标
]

# 需要从外层 icon 目录原样同步到项目内 icon 目录的 PNG（见文件头说明）
COPIES = ["n8n_launcher.png"]


def sync_png(name):
    src = os.path.join(SRC_DIR, name)
    dst = os.path.join(DST_DIR, name)

    if not os.path.isfile(src):
        print("[FAIL] 源文件不存在: %s" % src)
        return False

    shutil.copy2(src, dst)
    same = os.path.getsize(src) == os.path.getsize(dst)
    print("[COPY] %-24s -> 项目内 icon\\  %d bytes  %s"
          % (name, os.path.getsize(dst), "OK" if same else "SIZE MISMATCH"))
    return same


def build(src_name, dst_name):
    src = os.path.join(SRC_DIR, src_name)
    dst = os.path.join(DST_DIR, dst_name)

    if not os.path.isfile(src):
        print("[FAIL] 源文件不存在: %s" % src)
        return False

    with Image.open(src) as im:
        # 统一转 RGBA，保留 alpha 透明通道
        im = im.convert("RGBA")
        print("[SRC ] %-24s %dx%d %s" % (src_name, im.width, im.height, im.mode))

        # 为每个目标尺寸单独做 LANCZOS 缩放，而不是让 Pillow / Windows 代劳
        frames = []
        for s in SIZES:
            if im.width == s and im.height == s:
                frames.append(im.copy())
            else:
                frames.append(im.resize((s, s), Image.LANCZOS))

        # 以最大帧为主，其余通过 append_images 一并写入同一个 ICO 容器
        largest = frames[-1]
        largest.save(
            dst,
            format="ICO",
            sizes=[(s, s) for s in SIZES],
            append_images=frames[:-1],
        )

    print("[OK  ] %-24s -> %-20s %d bytes" % (src_name, dst_name, os.path.getsize(dst)))
    return True


def verify(dst_name):
    """反读 ICO，列出容器内实际包含的尺寸，确认七个尺寸都在。"""
    dst = os.path.join(DST_DIR, dst_name)
    with Image.open(dst) as im:
        found = sorted({(w, h) for (w, h) in im.ico.sizes()})
    got = [w for (w, h) in found]
    ok = got == SIZES
    print("[VER ] %-20s sizes=%s  %s" % (dst_name, got, "PASS" if ok else "MISMATCH"))
    return ok


def main():
    print("SRC_DIR = %s" % SRC_DIR)
    print("DST_DIR = %s" % DST_DIR)
    print("-" * 66)

    if not os.path.isdir(DST_DIR):
        print("[FAIL] 目标目录不存在: %s" % DST_DIR)
        return 1

    all_ok = True
    for name in COPIES:
        if not sync_png(name):
            all_ok = False

    for src_name, dst_name in TASKS:
        if not build(src_name, dst_name):
            all_ok = False

    print("-" * 66)
    for _, dst_name in TASKS:
        if not verify(dst_name):
            all_ok = False

    print("-" * 66)
    print("RESULT: %s" % ("ALL PASS" if all_ok else "HAS FAILURE"))
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())