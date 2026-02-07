import os
import argparse
import UnityPy
from UnityPy.streams import EndianBinaryWriter

def ensure_dir(path):
    if not os.path.exists(path):
        os.makedirs(path)

def unpack_recursive(input_dir, output_dir):
    print("开始递归解包...")
    total_files = 0
    total_extracted = 0

    for root, _, files in os.walk(input_dir):
        for filename in files:
            # 兼容 assetbundle 无后缀或 .bytes 后缀的文件
            if not (filename.endswith(".bytes") or "assetbundle" in filename.lower()):
                continue
            
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
                    # 仅处理 TextAsset 和 Texture2D 类型的对象
                    if obj.type.name in ["TextAsset", "Texture2D"]:
                        raw_data = obj.get_raw_data()
                        
                        # 暴力搜索 PNG 指纹 (89 50 4E 47 ...)
                        png_header = b'\x89PNG\r\n\x1a\n'
                        start_pos = raw_data.find(png_header)
                        
                        if start_pos != -1:
                            end_pos = raw_data.find(b'IEND', start_pos)
                            if end_pos != -1:
                                actual_png = raw_data[start_pos : end_pos + 8]
                                
                                # 命名规则：文件名.png 或 文件名_序号.png
                                save_name = f"{base_name}.png" if obj_idx == 0 else f"{base_name}_{obj_idx}.png"
                                with open(os.path.join(target_dir, save_name), "wb") as f:
                                    f.write(actual_png)
                                
                                print(f"[提取] {filename} -> {save_name}")
                                total_extracted += 1
                                obj_idx += 1
            except Exception as e:
                print(f"[错误] 跳过 {filename}: {e}")

    print("\n解包任务完成")
    print(f"统计汇总: 扫描文件数: {total_files}, 提取图片数: {total_extracted}")

def repack_recursive(input_dir, modded_dir, output_dir, use_compression=False):
    print("开始递归回封...")
    total_repacked = 0
    total_modified_objs = 0

    for root, _, files in os.walk(input_dir):
        for filename in files:
            if not (filename.endswith(".bytes") or "assetbundle" in filename.lower()):
                continue
            
            file_path = os.path.join(root, filename)
            rel_path = os.path.relpath(root, input_dir)
            mod_data_dir = os.path.join(modded_dir, rel_path)
            
            if not os.path.exists(mod_data_dir):
                continue

            try:
                env = UnityPy.load(file_path)
                modified = False
                base_name = os.path.splitext(filename)[0]
                
                obj_idx = 0
                for obj in env.objects:
                    if obj.type.name in ["TextAsset", "Texture2D"]:
                        # 对应解包时的命名规则匹配文件
                        img_name = f"{base_name}.png" if obj_idx == 0 else f"{base_name}_{obj_idx}.png"
                        img_path = os.path.join(mod_data_dir, img_name)
                        
                        if os.path.exists(img_path):
                            with open(img_path, "rb") as f:
                                new_png_data = f.read()
                            
                            # 手动重构 TextAsset 的二进制布局
                            # 布局: [Name Length][Name][Alignment][Data Length][Data][Alignment]
                            writer = EndianBinaryWriter()
                            
                            # 1. 写入资源名
                            name_bytes = base_name.encode('utf-8')
                            writer.write_int(len(name_bytes))
                            writer.write_bytes(name_bytes)
                            writer.align_stream(4)
                            
                            # 2. 写入数据流
                            writer.write_int(len(new_png_data))
                            writer.write_bytes(new_png_data)
                            writer.align_stream(4)
                            
                            # 覆盖原始对象数据
                            obj.set_raw_data(writer.bytes)
                            modified = True
                            total_modified_objs += 1
                            print(f"[Replace] {filename} <- {img_name}")
                            obj_idx += 1

                if modified:
                    target_root = os.path.join(output_dir, rel_path)
                    ensure_dir(target_root)
                    target_path = os.path.join(target_root, filename)
                    
                    with open(target_path, "wb") as f:
                        # 对于 Unity 6，使用 lz4 压缩
                        packer = "lz4" if use_compression else None
                        f.write(env.file.save(packer=packer))
                    total_repacked += 1
            except Exception as e:
                print(f"[ERROR] 回封 {filename} 失败: {e}")

    print("\n回封任务完成")
    print(f"统计汇总: 生成封包数: {total_repacked}, 替换资源总数: {total_modified_objs}")

def main():
    parser = argparse.ArgumentParser(description="UnityFS 图片资源批处理工具")
    subparsers = parser.add_subparsers(dest="command", required=True)

    # 解包参数
    up = subparsers.add_parser("unpack")
    up.add_argument("-i", "--input", default="pic", help="包含 .bytes 的源文件夹")
    up.add_argument("-o", "--output", default="pic_Extracted", help="导出图片的文件夹")

    # 回封参数
    re = subparsers.add_parser("repack")
    re.add_argument("-i", "--input", default="pic", help="包含原始 .bytes 的文件夹")
    re.add_argument("-m", "--modded", default="pic_Extracted", help="存放已修改图片的文件夹")
    re.add_argument("-o", "--output", default="pic_Repack", help="生成新封包的文件夹")
    re.add_argument("-c", "--compress", action="store_true", help="启用 LZ4 压缩")

    args = parser.parse_args()

    if args.command == "unpack":
        unpack_recursive(args.input, args.output)
    elif args.command == "repack":
        repack_recursive(args.input, args.modded, args.output, args.compress)

if __name__ == "__main__":
    main()