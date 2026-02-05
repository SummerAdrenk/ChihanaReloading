import os
import struct
import argparse
import textwrap
import sys

# 加密/解密算法
# Encrypt: (b << 2) | (b >> 6)  -> 循环左移 2 位
# Decrypt: (b << 6) | (b >> 2)  -> 循环右移 2 位 (即左移6位)

def rotate_left_2(data: bytes) -> bytes:
    # 加密: 循环左移 2 位
    res = bytearray(data)
    for i in range(len(res)):
        val = res[i]
        res[i] = ((val << 2) & 0xFF) | (val >> 6)
    return bytes(res)

def rotate_right_2(data: bytes) -> bytes:
    # 解密: 循环右移 2 位 (等同于 (data[i] << 6) | (data[i] >> 2))
    res = bytearray(data)
    for i in range(len(res)):
        val = res[i]
        res[i] = ((val << 6) & 0xFF) | (val >> 2)
    return bytes(res)

class ArcEntry:
    def __init__(self):
        self.name = ""
        self.size = 0
        self.offset = 0

class ArcManager:
    @staticmethod
    def unpack(arc_path, output_dir, do_decrypt=False):
        print(f"\n>> Command: Unpack")
        print(f"   输入: {arc_path}")
        print(f"   输出: {output_dir}")
        print(f"   解密: {'是' if do_decrypt else '否'}")

        if not os.path.exists(arc_path):
            print(f"[ERROR] 找不到输入文件: {arc_path}")
            return

        os.makedirs(output_dir, exist_ok=True)

        with open(arc_path, 'rb') as f:
            # 读取头部
            file_count = struct.unpack('<I', f.read(4))[0]
            index_size = struct.unpack('<I', f.read(4))[0]
            base_offset = 8 + index_size
            
            print(f"[INFO] 发现 {file_count} 个文件，索引大小 {index_size} 字节。")
            
            entries = []
            
            # 读取索引
            index_end_pos = 8 + index_size
            
            for _ in range(file_count):
                if f.tell() >= index_end_pos:
                    raise ValueError("索引读取超出边界...")
                
                entry = ArcEntry()
                entry.size = struct.unpack('<I', f.read(4))[0]
                raw_offset = struct.unpack('<I', f.read(4))[0]
                entry.offset = base_offset + raw_offset
                
                # 读取文件名
                name_bytes = bytearray()
                while True:
                    char_bytes = f.read(2)
                    if not char_bytes or char_bytes == b'\x00\x00':
                        break
                    name_bytes.extend(char_bytes)
                
                entry.name = name_bytes.decode('utf-16-le')
                entries.append(entry)

            # 提取文件
            print(">>开始提取文件")
            for entry in entries:
                print(f"  -> 提取中: {entry.name}", end="")
                
                f.seek(entry.offset)
                data = f.read(entry.size)
                
                # 解密逻辑
                if do_decrypt and entry.name.lower().endswith('.ws2'):
                    print(" (解密中)", end="")
                    data = rotate_right_2(data)
                
                # 写入文件
                out_path = os.path.join(output_dir, entry.name)
                os.makedirs(os.path.dirname(out_path), exist_ok=True)
                
                with open(out_path, 'wb') as out_f:
                    out_f.write(data)
                print(" ...完成")

    @staticmethod
    def pack(input_dir, arc_path, do_encrypt=False):
        print(f"\n>> Command: Pack")
        print(f"   输入: {input_dir}")
        print(f"   输出: {arc_path}")
        print(f"   加密: {'是' if do_encrypt else '否'}")

        files_to_pack = []
        if os.path.exists(input_dir):
            for root, dirs, files in os.walk(input_dir):
                for file in files:
                    files_to_pack.append(os.path.join(root, file))
        else:
            print(f"[ERROR] 输入文件夹不存在: {input_dir}")
            return
        
        if not files_to_pack:
            print("[WARNNING] 输入文件夹为空！")
            return

        os.makedirs(os.path.dirname(os.path.abspath(arc_path)), exist_ok=True)

        # 预计算索引大小
        index_size = 0
        entries = []
        
        for file_path in files_to_pack:
            file_name = os.path.basename(file_path)
            entry_meta_size = 8 
            name_bytes = file_name.encode('utf-16-le')
            entry_meta_size += len(name_bytes) + 2
            
            index_size += entry_meta_size
            
            entry = ArcEntry()
            entry.name = file_name
            entry.path = file_path
            entries.append(entry)

        base_offset = 8 + index_size
        current_data_offset = 0

        with open(arc_path, 'wb') as f:
            # 写入数据
            f.seek(base_offset)
            print(">>开始写入文件数据")
            for entry in entries:
                print(f"  -> 打包中: {entry.name}", end="")
                
                with open(entry.path, 'rb') as in_f:
                    data = in_f.read()
                
                if do_encrypt and entry.name.lower().endswith('.ws2'):
                    print(" (加密中)", end="")
                    data = rotate_left_2(data)
                
                entry.offset = current_data_offset
                entry.size = len(data)
                
                f.write(data)
                current_data_offset += len(data)
                print(" ...完成")
            
            # 写入索引
            print(">>开始写入索引")
            f.seek(0)
            f.write(struct.pack('<I', len(entries)))
            f.write(struct.pack('<I', index_size))
            
            for entry in entries:
                f.write(struct.pack('<I', entry.size))
                f.write(struct.pack('<I', entry.offset))
                f.write(entry.name.encode('utf-16-le'))
                f.write(b'\x00\x00')

def process_enc_dec_file(path, output_path, mode):
    print(f"  -> Processing: {os.path.basename(path)}")
    with open(path, 'rb') as f:
        data = f.read()
    
    if mode == 'enc':
        data = rotate_left_2(data)
    else:
        data = rotate_right_2(data)
        
    with open(output_path, 'wb') as f:
        f.write(data)

def batch_process_enc_dec(input_path, output_path, mode):
    print(f"\n>> Command: {'Enc' if mode=='enc' else 'Dec'}")
    print(f"   输入: {input_path}")
    print(f"   输出: {output_path}")

    if os.path.isfile(input_path):
        os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
        process_enc_dec_file(input_path, output_path, mode)
    elif os.path.isdir(input_path):
        os.makedirs(output_path, exist_ok=True)
        files = []
        for f in os.listdir(input_path):
            if f.lower().endswith('.ws2') or f.lower().endswith('.wsc'):
                files.append(os.path.join(input_path, f))
        
        for file_path in files:
            out_file = os.path.join(output_path, os.path.basename(file_path))
            process_enc_dec_file(file_path, out_file, mode)
    else:
        print(f"[ERROR] 输入路径不存在: {input_path}")

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
    AdvHD Arc/Ws2 Tool _Ver1.0.0
    usage: python arc.py <command> [-i INPUT] [-o OUTPUT] [options]
    """)

    parser = argparse.ArgumentParser(
        description=desc_text,
        formatter_class=argparse.RawTextHelpFormatter,
        usage=argparse.SUPPRESS 
    )
    
    subparsers = parser.add_subparsers(dest='command', title="Available Commands", metavar="")

    # Unpack
    p_unpack = subparsers.add_parser('unpack', help='解包 .arc 文件')
    p_unpack.add_argument('-i', '--input', default=None, help='输入 .arc 文件路径')
    p_unpack.add_argument('-o', '--output', default=None, help='输出文件夹路径')
    p_unpack.add_argument('-dec', '--decrypt', action='store_true', help='同时解密 .ws2 文件')

    # Pack
    p_pack = subparsers.add_parser('pack', help='封包为 .arc 文件')
    p_pack.add_argument('-i', '--input', default=None, help='输入文件夹路径')
    p_pack.add_argument('-o', '--output', default=None, help='输出 .arc 文件路径')
    p_pack.add_argument('-enc', '--encrypt', action='store_true', help='同时加密 .ws2 文件')

    # Enc
    p_enc = subparsers.add_parser('enc', help='加密文件或文件夹')
    p_enc.add_argument('-i', '--input', default=None, help='输入路径')
    p_enc.add_argument('-o', '--output', default=None, help='输出路径')

    # Dec
    p_dec = subparsers.add_parser('dec', help='解密文件或文件夹')
    p_dec.add_argument('-i', '--input', default=None, help='输入路径')
    p_dec.add_argument('-o', '--output', default=None, help='输出路径')

    args = parser.parse_args()

    if args.command == 'unpack':
        final_input = get_arg(args.input, "输入 .arc 文件路径", "Rio1.arc")
        final_output = get_arg(args.output, "输出文件夹路径", "Rio1")
        ArcManager.unpack(final_input, final_output, args.decrypt)

    elif args.command == 'pack':
        final_input = get_arg(args.input, "输入文件夹路径", "Rio1_enc")
        final_output = get_arg(args.output, "输出 .arc 文件路径", "Rio1.chs")
        ArcManager.pack(final_input, final_output, args.encrypt)

    elif args.command == 'enc':
        final_input = get_arg(args.input, "输入路径", "Rio1_release")
        final_output = get_arg(args.output, "输出路径", "Rio1_enc")
        batch_process_enc_dec(final_input, final_output, 'enc')

    elif args.command == 'dec':
        final_input = get_arg(args.input, "输入路径", "Rio1")
        final_output = get_arg(args.output, "输出路径", "Rio1_dec")
        batch_process_enc_dec(final_input, final_output, 'dec')

    else:
        parser.print_help()

    if len(sys.argv) < 2:
        input("\n按回车键退出...")