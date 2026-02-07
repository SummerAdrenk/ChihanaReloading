import os
import argparse
import UnityPy
from UnityPy.streams import EndianBinaryWriter

#适用于使用@ARCH000封包（xxx.arc）的UnityFS文件批处理工具

def ensure_dir(path):
    if not os.path.exists(path):
        os.makedirs(path)

def unpack_recursive(input_dir, output_dir):
    print(f"开始解包")
    print(f"输入: {input_dir}")
    print(f"输出: {output_dir}")
    
    if not os.path.exists(input_dir):
        print(f"[ERROR] 找不到输入文件夹 '{input_dir}'")
        return

    total_files = 0
    
    for root, dirs, files in os.walk(input_dir):
        for filename in files:
            file_path = os.path.join(root, filename)
            # 保持原始文件的目录层级
            rel_path = os.path.relpath(root, input_dir)
            
            try:
                env = UnityPy.load(file_path)
            except:
                continue 

            current_output_dir = os.path.join(output_dir, rel_path)
            
            extracted_count = 0
            for obj in env.objects:
                if obj.type.name == "TextAsset":
                    reader = obj.reader
                    reader.Position = obj.byte_start
                    try:
                        name_len = reader.read_int()
                        name_bytes = reader.read_bytes(name_len)
                        name = name_bytes.decode('utf-8')
                        reader.align_stream()
                        script_size = reader.read_int()
                        script_bytes = reader.read_bytes(script_size)

                        ensure_dir(current_output_dir)
                        out_path = os.path.join(current_output_dir, f"{name}.lua.txt")
                        
                        # 简单的防覆盖提示
                        if os.path.exists(out_path):
                            print(f"[WARNNING] 文件重名! {name}.lua.txt 被 {filename} 中的资源覆盖了。")

                        with open(out_path, "wb") as f:
                            f.write(script_bytes)
                        extracted_count += 1
                    except Exception as e:
                        pass

            if extracted_count > 0:
                print(f"解包: {filename} -> {extracted_count} 个文件")
                total_files += 1

    print(f"解包完成！")

def repack_recursive(src_dir, modded_dir, output_dir, use_compression=False):
    print(f"开始回封")
    print(f"原始文件: {src_dir}")
    print(f"修改资源: {modded_dir}")
    print(f"输出位置: {output_dir}")
    print(f"压缩模式: {'LZ4' if use_compression else '无压缩'}")

    if not os.path.exists(src_dir):
        print(f"[ERROR] 找不到原始文件夹 '{src_dir}'")
        return

    total_files = 0

    for root, dirs, files in os.walk(src_dir):
        for filename in files:
            source_path = os.path.join(root, filename)
            rel_path = os.path.relpath(root, src_dir)
            current_mod_dir = os.path.join(modded_dir, rel_path)
            
            if not os.path.exists(current_mod_dir):
                continue

            try:
                env = UnityPy.load(source_path)
            except:
                continue

            modified_count = 0
            for obj in env.objects:
                if obj.type.name == "TextAsset":
                    reader = obj.reader
                    reader.Position = obj.byte_start
                    try:
                        name_len = reader.read_int()
                        name_bytes = reader.read_bytes(name_len)
                        name = name_bytes.decode('utf-8')

                        # 直接找文件名.lua.txt
                        mod_file_path = os.path.join(current_mod_dir, f"{name}.lua.txt")
                        
                        if os.path.exists(mod_file_path):
                            with open(mod_file_path, "rb") as f:
                                new_bytes = f.read()
                            
                            writer = EndianBinaryWriter(endian=reader.endian)
                            writer.write_int(len(name_bytes))
                            writer.write_bytes(name_bytes)
                            writer.align_stream()
                            writer.write_int(len(new_bytes))
                            writer.write_bytes(new_bytes)
                            writer.align_stream()
                            
                            obj.set_raw_data(writer.bytes)
                            modified_count += 1
                    except:
                        pass
            
            if modified_count > 0:
                target_dir = os.path.join(output_dir, rel_path)
                ensure_dir(target_dir)
                target_path = os.path.join(target_dir, filename)
                
                # 根据参数决定是否使用压缩
                with open(target_path, "wb") as f:
                    if use_compression:
                        f.write(env.file.save(packer="lz4"))
                    else:
                        f.write(env.file.save())
                
                print(f"回封: {filename} (修改了 {modified_count} 处)")
                total_files += 1

    print(f"回封完成！")

def main():
    parser = argparse.ArgumentParser(description="UnityFS文本批处理工具")
    subparsers = parser.add_subparsers(dest="command", required=True)

    parser_unpack = subparsers.add_parser("unpack")
    parser_unpack.add_argument("-i", "--input", default="src")
    parser_unpack.add_argument("-o", "--output", default="src_Extracted")

    parser_repack = subparsers.add_parser("repack")
    parser_repack.add_argument("-i", "--input", default="src")
    parser_repack.add_argument("-m", "--modded", default="src_Fixed")
    parser_repack.add_argument("-o", "--output", default="src_Repack")
    parser_repack.add_argument("-c", "--compress", action="store_true", help="启用LZ4压缩 (默认不压缩)")

    args = parser.parse_args()

    if args.command == "unpack":
        unpack_recursive(args.input, args.output)
    elif args.command == "repack":
        repack_recursive(args.input, args.modded, args.output, args.compress)

if __name__ == "__main__":
    main()