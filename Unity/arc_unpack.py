import struct
import os
import sys

def read_string(file):
    length_bytes = file.read(1)
    if not length_bytes: return ""
    length = struct.unpack('B', length_bytes)[0]
    if length == 0: return ""
    data = file.read(length)
    return data.decode('utf-8')

def unpack_single_arc(arc_path):
    arc_dir = os.path.dirname(arc_path)
    arc_name = os.path.splitext(os.path.basename(arc_path))[0]
    output_dir = os.path.join(arc_dir, arc_name)
    
    print(f"-> 正在解包: {os.path.basename(arc_path)}")
    os.makedirs(output_dir, exist_ok=True)
    
    with open(arc_path, 'rb') as f:
        magic = f.read(8)
        if magic == b'@ARCH000':
            ts = read_string(f)
            # arc名_timestamp.txt
            ts_path = os.path.join(arc_dir, f"{arc_name}_timestamp.txt")
            with open(ts_path, "w", encoding="utf-8") as tf:
                tf.write(ts)
        
        f.seek(-8, 2)
        index_offset = struct.unpack('<Q', f.read(8))[0]
        f.seek(index_offset)
        
        count = struct.unpack('<I', f.read(4))[0]
        for _ in range(count):
            fname = read_string(f)
            f_offset = struct.unpack('<Q', f.read(8))[0]
            f_size = struct.unpack('<Q', f.read(8))[0]
            f.read(1) # skip 0x4E
            f_path = read_string(f)
            
            if f_size > 0:
                curr = f.tell()
                f.seek(f_offset)
                target_path = os.path.join(output_dir, f_path.strip('/'))
                os.makedirs(target_path, exist_ok=True)
                with open(os.path.join(target_path, fname), 'wb') as out:
                    out.write(f.read(f_size))
                f.seek(curr)

def main():
    if len(sys.argv) < 2:
        print("用法: python arc_unpack.py <目标文件夹>")
        return
    input_folder = sys.argv[1]
    if not os.path.isdir(input_folder): return
    files = [f for f in os.listdir(input_folder) if f.lower().endswith('.arc')]
    for f in files:
        unpack_single_arc(os.path.join(input_folder, f))
    print("\n批量解包完成。时间戳已提取至根目录。")

if __name__ == "__main__":
    main()