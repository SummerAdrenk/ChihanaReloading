import os
import argparse
import UnityPy
from UnityPy.streams import EndianBinaryWriter

# Unity 6 暴力提取与回封

def ensure_dir(path):
    if not os.path.exists(path):
        os.makedirs(path)

def unpack_recursive(input_dir, output_dir):
    print(f"开始递归解包...")
    total_files = 0
    total_extracted = 0

    for root, _, files in os.walk(input_dir):
        for filename in files:
            if not filename.endswith(".bytes"): continue
            
            file_path = os.path.join(root, filename)
            rel_path = os.path.relpath(root, input_dir)
            target_dir = os.path.join(output_dir, rel_path)
            
            try:
                env = UnityPy.load(file_path)
                ensure_dir(target_dir)
                total_files += 1
                
                base_name = os.path.splitext(filename)[0]
                obj_idx = 0
                
                for obj in env.objects:
                    # 识别 TextAsset 或 Texture2D 对象
                    if obj.type.name in ["TextAsset", "Texture2D"]:
                        raw_data = obj.get_raw_data()
                        
                        # 搜索 PNG 签名
                        png_header = b'\x89PNG\r\n\x1a\n'
                        start_pos = raw_data.find(png_header)
                        
                        if start_pos != -1:
                            end_pos = raw_data.find(b'IEND', start_pos)
                            if end_pos != -1:
                                actual_png = raw_data[start_pos : end_pos + 8]
                                
                                save_name = f"{base_name}.png" if obj_idx == 0 else f"{base_name}_{obj_idx}.png"
                                with open(os.path.join(target_dir, save_name), "wb") as f:
                                    f.write(actual_png)
                                
                                print(f"[提取] {filename} -> {save_name}")
                                total_extracted += 1
                                obj_idx += 1
            except Exception as e:
                print(f"[ERROR] 跳过 {filename}: {e}")

    print(f"\n解包完成！")
    print(f"统计: 处理了 {total_files} 个容器文件，成功提取出 {total_extracted} 张图片。")

def repack_recursive(input_dir, modded_dir, output_dir, use_compression=False):
    print(f"开始递归回封...")
    total_repacked = 0
    total_modified_objs = 0

    for root, _, files in os.walk(input_dir):
        for filename in files:
            if not filename.endswith(".bytes"): continue
            
            file_path = os.path.join(root, filename)
            rel_path = os.path.relpath(root, input_dir)
            mod_data_dir = os.path.join(modded_dir, rel_path)
            
            if not os.path.exists(mod_data_dir): continue

            try:
                env = UnityPy.load(file_path)
                modified = False
                base_name = os.path.splitext(filename)[0]
                
                obj_idx = 0
                for obj in env.objects:
                    if obj.type.name in ["TextAsset", "Texture2D"]:
                        img_name = f"{base_name}.png" if obj_idx == 0 else f"{base_name}_{obj_idx}.png"
                        img_path = os.path.join(mod_data_dir, img_name)
                        
                        if os.path.exists(img_path):
                            with open(img_path, "rb") as f:
                                new_png_data = f.read()
                            
                            # 手动构建二进制块，确保符合 Unity 序列化对齐要求
                            writer = EndianBinaryWriter()
                            # 写入资源名
                            writer.write_string_to_unicode(base_name)
                            # 4字节对齐
                            writer.align_stream(4)
                            # 写入图片长度
                            writer.write_int(len(new_png_data))
                            # 写入图片数据
                            writer.write_bytes(new_png_data)
                            # 结尾对齐
                            writer.align_stream(4)
                            
                            obj.set_raw_data(writer.bytes)
                            modified = True
                            total_modified_objs += 1
                            print(f"[MOD] {filename} <- {img_name}")
                            obj_idx += 1

                if modified:
                    target_root = os.path.join(output_dir, rel_path)
                    ensure_dir(target_root)
                    target_path = os.path.join(target_root, filename)
                    
                    with open(target_path, "wb") as f:
                        packer = "lz4" if use_compression else None
                        f.write(env.file.save(packer=packer))
                    total_repacked += 1
            except Exception as e:
                print(f"[ERROR] 回封 {filename} 失败: {e}")

    print(f"\n任务结束！")
    print(f"统计: 修改并生成了 {total_repacked} 个封包，共替换图片 {total_modified_objs} 处。")

def main():
    parser = argparse.ArgumentParser(description="UnityFS图片批处理工具")
    subparsers = parser.add_subparsers(dest="command", required=True)

    up = subparsers.add_parser("unpack")
    up.add_argument("-i", "--input", default="pic")
    up.add_argument("-o", "--output", default="pic_Extracted")

    re = subparsers.add_parser("repack")
    re.add_argument("-i", "--input", default="pic")
    re.add_argument("-m", "--modded", default="pic_Extracted")
    re.add_argument("-o", "--output", default="pic_Repack")
    re.add_argument("-c", "--compress", action="store_true")

    args = parser.parse_args()

    if args.command == "unpack":
        unpack_recursive(args.input, args.output)
    elif args.command == "repack":
        repack_recursive(args.input, args.modded, args.output, args.compress)

if __name__ == "__main__":
    main()