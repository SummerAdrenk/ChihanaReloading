import struct
import os
import sys
import argparse
import textwrap
from functools import cmp_to_key

def write_string(file, string):
    encoded = string.encode('utf-8')
    length = min(len(encoded), 255)
    file.write(struct.pack('B', length))
    file.write(encoded[:length])
    return 1 + length

def csharp_filename_compare(x_full, y_full):
    # 对整个相对路径进行排序
    # x_full结构: (full_path, filename, rel_path) -> 取 [2]
    x = x_full[2] 
    y = y_full[2]
    
    x = x if x else ""
    y = y if y else ""

    i = 0
    j = 0
    while i < len(x) and j < len(y):
        if x[i] != y[j]:
            break
        i += 1
        j += 1
    
    a_char = x[i] if i < len(x) else 0
    b_char = y[j] if j < len(y) else 0
    
    # 获取字符的 ASCII 码
    # 若是结束符(0)，保持为0
    a_code = ord(a_char) if isinstance(a_char, str) else 0
    b_code = ord(b_char) if isinstance(b_char, str) else 0

    if a_char == '_' and ('a' <= b_char <= 'z'):
        return 1
    elif b_char == '_' and ('a' <= a_char <= 'z'):
        return -1
    
    return a_code - b_code

def pack_single_folder(folder_path, output_dir):
    folder_name = os.path.basename(folder_path)
    arc_path = os.path.join(output_dir, folder_name + ".arc")
    
    print(f"  -> 正在打包: {folder_name}")
    
    parent_dir = os.path.dirname(folder_path)
    ts = "20241122095209" 
    ts_file = os.path.join(parent_dir, f"{folder_name}_timestamp.txt")
    
    if os.path.exists(ts_file):
        with open(ts_file, 'r', encoding='utf-8') as tf:
            ts = tf.read().strip()
            print(f"   已读取时间戳: {ts}")

    file_list = []
    for root, _, files in os.walk(folder_path):
        for f in files:
            full_path = os.path.join(root, f)
            rel_path = os.path.relpath(root, folder_path).replace("\\", "/")
            if rel_path == ".": rel_path = ""
            # 保存元组 (完整路径, 文件名, 相对目录)
            file_list.append((full_path, f, rel_path))

    file_list.sort(key=cmp_to_key(csharp_filename_compare))

    with open(arc_path, 'wb') as f:
        f.write(b'@ARCH000')
        write_string(f, ts)
        
        entries = []
        for full, name, rel in file_list:
            offset = f.tell()
            with open(full, 'rb') as src:
                data = src.read()
                f.write(data)

            final_name = name
            if final_name.lower().endswith('.unity3d'):
                final_name = os.path.splitext(final_name)[0]

            entries.append((final_name, offset, len(data), rel))
            
        index_start = f.tell()
        f.write(struct.pack('<I', len(entries)))
        for name, offset, size, rel in entries:
            write_string(f, name)
            f.write(struct.pack('<Q', offset))
            f.write(struct.pack('<Q', size))
            f.write(struct.pack('B', 0x4E))
            write_string(f, rel)
        f.write(struct.pack('<Q', index_start))

def batch_pack(input_folder, output_folder):
    if not os.path.isdir(input_folder):
        print(f"[ERROR] 输入路径 '{input_folder}' 不存在")
        return

    print(f"开始处理...")
    print(f"   输入: {input_folder}")
    print(f"   输出: {output_folder}")
    os.makedirs(output_folder, exist_ok=True)
    
    subfolders = [os.path.join(input_folder, d) for d in os.listdir(input_folder) 
                  if os.path.isdir(os.path.join(input_folder, d))]
    
    if not subfolders:
        print("[ERROR] 未找到待打包的子文件夹...")
        return

    for folder in subfolders:
        pack_single_folder(folder, output_folder)
        
    print("\n批量封包完成...")
    print("="*50)

def get_arg(value_from_args, prompt_text, default_val):
    if value_from_args is not None:
        return value_from_args
    
    user_in = input(f"{prompt_text} (默认: {default_val}): ").strip()
    if not user_in:
        return default_val
    return user_in.strip('"')

if __name__ == "__main__":
    desc_text = textwrap.dedent("""\
    ================================================
    Unity Archive Packer (folder -> .arc)
    usage: python arc_pack.py [-i INPUT] [-o OUTPUT]
    """)

    parser = argparse.ArgumentParser(
        description=desc_text,
        formatter_class=argparse.RawTextHelpFormatter,
        usage=argparse.SUPPRESS 
    )

    parser.add_argument("-i", "--input", default=None, help="待打包的文件夹根目录")
    parser.add_argument("-o", "--output", default=None, help=".arc 输出路径")
    
    args = parser.parse_args()

    if len(sys.argv) == 1:
        print(desc_text)

    final_input = get_arg(args.input, "请输入待打包文件夹路径", "arc_release")
    final_output = get_arg(args.output, "请输入输出路径", "arc_new")

    batch_pack(final_input, final_output)

    if len(sys.argv) == 1:
        input("\n按回车键退出...")