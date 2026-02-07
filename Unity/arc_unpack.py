import struct
import os
import sys
import argparse
import textwrap

def read_string(file):
    length_bytes = file.read(1)
    if not length_bytes: return ""
    length = struct.unpack('B', length_bytes)[0]
    if length == 0: return ""
    data = file.read(length)
    return data.decode('utf-8')

def unpack_single_arc(arc_path, target_root_dir):
    arc_name = os.path.splitext(os.path.basename(arc_path))[0]
    output_dir = os.path.join(target_root_dir, arc_name)
    
    print(f"  -> 正在解包: {os.path.basename(arc_path)}")
    os.makedirs(output_dir, exist_ok=True)
    
    with open(arc_path, 'rb') as f:
        magic = f.read(8)
        if magic == b'@ARCH000':
            ts = read_string(f)
            # arc名_timestamp.txt
            ts_path = os.path.join(target_root_dir, f"{arc_name}_timestamp.txt")
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
                
                # 0x53467974696E55
                header_check = f.read(8)
                f.seek(f_offset)

                if header_check.startswith(b'UnityFS'):
                    if not fname.endswith('.unity3d'):
                        fname += '.unity3d'

                safe_path = f_path.strip('/').replace('\\', '/')
                target_path = os.path.join(output_dir, safe_path)
                
                os.makedirs(target_path, exist_ok=True)
                with open(os.path.join(target_path, fname), 'wb') as out:
                    out.write(f.read(f_size))
                f.seek(curr)

def batch_unpack(input_folder, output_folder):
    if not os.path.isdir(input_folder):
        print(f"[ERROR] 输入路径 '{input_folder}' 不存在")
        return

    print(f"开始处理...")
    print(f"   输入: {input_folder}")
    print(f"   输出: {output_folder}")
    os.makedirs(output_folder, exist_ok=True)

    files = [f for f in os.listdir(input_folder) if f.lower().endswith('.arc')]
    
    if not files:
        print("[ERROR] 未找到 .arc 文件。")
        return

    for f in files:
        unpack_single_arc(os.path.join(input_folder, f), output_folder)
        
    print(f"\n批量解包完成，时间戳已提取至{output_folder}...")
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
    Unity Archive Unpacker (.arc -> folder)
    usage: python arc_unpack.py [-i INPUT] [-o OUTPUT]
    """)

    parser = argparse.ArgumentParser(
        description=desc_text,
        formatter_class=argparse.RawTextHelpFormatter,
        usage=argparse.SUPPRESS 
    )

    parser.add_argument("-i", "--input", default=None, help="包含 .arc 文件的输入文件夹")
    parser.add_argument("-o", "--output", default=None, help="解包输出路径")
    
    args = parser.parse_args()

    if len(sys.argv) == 1:
        print(desc_text)

    final_input = get_arg(args.input, "请输入 .arc 文件夹路径", "arc")
    final_output = get_arg(args.output, "请输入输出路径", "arc_unpack")

    batch_unpack(final_input, final_output)

    if len(sys.argv) == 1:
        input("\n按回车键退出...")