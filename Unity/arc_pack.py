import struct
import os
import sys

def write_string(file, string):
    encoded = string.encode('utf-8')
    length = min(len(encoded), 255)
    file.write(struct.pack('B', length))
    file.write(encoded[:length])
    return 1 + length

def pack_single_folder(folder_path):
    parent_dir = os.path.dirname(folder_path)
    folder_name = os.path.basename(folder_path)
    arc_path = folder_path + ".arc"
    
    print(f"-> 正在打包: {folder_name}")
    
    # [文件夹名]_timestamp.txt
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
            file_list.append((full_path, f, rel_path))

    with open(arc_path, 'wb') as f:
        f.write(b'@ARCH000')
        write_string(f, ts)
        
        entries = []
        for full, name, rel in file_list:
            offset = f.tell()
            with open(full, 'rb') as src:
                data = src.read()
                f.write(data)
            entries.append((name, offset, len(data), rel))
            
        index_start = f.tell()
        f.write(struct.pack('<I', len(entries)))
        for name, offset, size, rel in entries:
            write_string(f, name)
            f.write(struct.pack('<Q', offset))
            f.write(struct.pack('<Q', size))
            f.write(struct.pack('B', 0x4E))
            write_string(f, rel)
        f.write(struct.pack('<Q', index_start))

def main():
    if len(sys.argv) < 2:
        print("用法: python arc_pack.py <目标文件夹>")
        return
    parent_folder = sys.argv[1]
    subfolders = [os.path.join(parent_folder, d) for d in os.listdir(parent_folder) 
                  if os.path.isdir(os.path.join(parent_folder, d))]
    for folder in subfolders:
        pack_single_folder(folder)
    print("\n批量封包完成。")

if __name__ == "__main__":
    main()