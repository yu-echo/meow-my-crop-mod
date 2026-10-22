"""把 BepInEx + 插件打成一个 payload.zip，供安装器内嵌。

用法：
    python tools/make_payload.py --game "C:\...\Meow My Crop!" \
        --dll  src/MeowAutomation/bin/Release/MeowAutomation.dll \
        --icon pig_icon.png

生成 installer/assets/payload.zip，结构就是游戏根目录的相对路径。
"""
import argparse
import os
import sys
import zipfile

ROOT_FILES = ["winhttp.dll", "doorstop_config.ini", ".doorstop_version"]


def add(zf, path, arcname):
    if not os.path.isfile(path):
        print("  跳过（不存在）:", path)
        return 0
    zf.write(path, arcname)
    return 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", required=True, help="游戏根目录（含 BepInEx）")
    ap.add_argument("--dll", required=True, help="编译好的 MeowAutomation.dll")
    ap.add_argument("--icon", default=None, help="pig_icon.png（可选）")
    ap.add_argument("--out", default=os.path.join("installer", "assets", "payload.zip"))
    a = ap.parse_args()

    bep = os.path.join(a.game, "BepInEx")
    if not os.path.isdir(bep):
        sys.exit("找不到 BepInEx 目录: " + bep)

    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    n = 0
    with zipfile.ZipFile(a.out, "w", zipfile.ZIP_DEFLATED) as zf:
        for f in ROOT_FILES:
            n += add(zf, os.path.join(a.game, f), f)
        for base, _, files in os.walk(os.path.join(bep, "core")):
            for f in files:
                full = os.path.join(base, f)
                rel = os.path.relpath(full, a.game).replace(os.sep, "/")
                n += add(zf, full, rel)
        n += add(zf, a.dll, "BepInEx/plugins/MeowAutomation.dll")
        if a.icon:
            n += add(zf, a.icon, "BepInEx/plugins/pig_icon.png")

    print("已生成", a.out, "共", n, "个文件，大小", os.path.getsize(a.out), "字节")


if __name__ == "__main__":
    main()
