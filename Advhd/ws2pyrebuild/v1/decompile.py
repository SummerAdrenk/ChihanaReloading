from WS2FILE import *
import os

# 手动输入路径
scr_path = input("请输入待解包的 .ws2 文件夹路径 (例如 scr): ")
out_path = input("请输入解包后的文本输出路径 (例如 rio_dump): ")

files = os.listdir(scr_path)
os.makedirs(out_path, exist_ok=True)

for file in files:
    if not file.endswith(".ws2"):
        continue
    print(f"Processing {file}...")
    with open(os.path.join(scr_path, file), "rb") as f:
        data = f.read()
    dumper = WS2FileDumper(data)
    output_file = os.path.join(out_path, file + ".txt")
    dumper.dump(output_file)