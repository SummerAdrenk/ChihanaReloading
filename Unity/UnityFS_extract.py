import os
import argparse
import UnityPy
from PIL import Image
import io

# UnityFS提取（半成品）

def ensure_dir(path):
    if not os.path.exists(path):
        os.makedirs(path)

def get_extension(obj):
    type_name = obj.type.name
    if type_name in ["Texture2D", "Sprite"]: return ".png"
    if type_name == "Font": return ".ttf"
    if type_name == "AudioClip": return ".wav"
    if type_name == "TextAsset": return ".txt"
    return None

def find_font_start(raw_data):
    """搜索标准字体文件的起始位置"""
    # TrueType: 00 01 00 00, OpenType: OTTO
    offsets = [raw_data.find(b'\x00\x01\x00\x00'), raw_data.find(b'OTTO')]
    valid_offsets = [o for o in offsets if o != -1]
    return min(valid_offsets) if valid_offsets else -1

def unpack_recursive(input_dir, output_dir):
    print(f"[*] 开始解包... 目标目录: {input_dir}")
    stats = {"files": 0, "extracted": 0}
    
    for root, _, files in os.walk(input_dir):
        for filename in files:
            if filename.endswith((".py", ".exe", ".dll", ".resS", ".resource")): continue
            file_path = os.path.join(root, filename)
            rel_path = os.path.relpath(root, input_dir)
            
            try:
                env = UnityPy.load(file_path)
                stats["files"] += 1
                extracted_ids = set()

                # 1. Container 映射提取
                if env.container:
                    for container_path, obj in env.container.items():
                        ext = get_extension(obj)
                        if ext:
                            save_name = container_path.replace("/", "_").replace("\\", "_")
                            if process_obj(obj, save_name, filename, rel_path, output_dir):
                                extracted_ids.add(obj.path_id)
                                stats["extracted"] += 1

                # 2. Objects 兜底提取
                for obj in env.objects:
                    if obj.path_id in extracted_ids: continue
                    ext = get_extension(obj)
                    if ext:
                        res_name = obj.peek_name() or str(obj.path_id)
                        if process_obj(obj, res_name + ext, filename, rel_path, output_dir):
                            stats["extracted"] += 1
                            
            except Exception as e:
                print(f"[!] 跳过 {filename}: {e}")

    print(f"\n[√] 解包完成！提取资源: {stats['extracted']}")

def process_obj(obj, res_path, original_filename, rel_path, output_dir):
    target_dir = os.path.join(output_dir, rel_path, original_filename + "_data")
    ensure_dir(target_dir)
    
    save_name = res_path.replace("/", "_").replace("\\", "_")
    ext = get_extension(obj)
    if ext and not save_name.lower().endswith(ext):
        save_name += ext
    save_path = os.path.join(target_dir, save_name)

    try:
        data = obj.read()
        # 剔除 Unity 序列化头
        if obj.type.name == "Font":
            raw = obj.get_raw_data()
            start_pos = find_font_start(raw)
            if start_pos != -1:
                with open(save_path, "wb") as f:
                    f.write(raw[start_pos:]) # 只提取真正的字体数据
                return True
            else:
                # 备选方案: 读取 m_FontData 字段
                font_data = getattr(data, "m_FontData", b"")
                if font_data:
                    with open(save_path, "wb") as f:
                        f.write(font_data)
                    return True

        # 图片处理
        elif obj.type.name in ["Texture2D", "Sprite"]:
            if hasattr(data, "image") and data.image:
                data.image.save(save_path)
                return True
        
        # 音频与文本
        elif obj.type.name == "AudioClip":
            if hasattr(data, "samples"):
                for name, audio_data in data.samples.items():
                    with open(os.path.join(target_dir, name), "wb") as f:
                        f.write(audio_data)
                return True
        elif obj.type.name == "TextAsset":
            content = data.script
            with open(save_path, "wb") as f:
                f.write(content if isinstance(content, bytes) else content.encode('utf-8'))
            return True
    except:
        pass
    return False

def repack_recursive(input_dir, modded_dir, output_dir, use_compression=True):
    print(f"[*] 开始回封... 来源: {modded_dir}")
    stats = {"repacked": 0, "modified": 0}
    
    for root, _, files in os.walk(input_dir):
        for filename in files:
            file_path = os.path.join(root, filename)
            rel_path = os.path.relpath(root, input_dir)
            mod_data_dir = os.path.join(modded_dir, rel_path, filename + "_data")
            
            if not os.path.exists(mod_data_dir): continue

            try:
                env = UnityPy.load(file_path)
                is_changed = False
                obj_map = {}

                # 建立映射
                if env.container:
                    for path, obj in env.container.items():
                        ext = get_extension(obj)
                        if ext:
                            clean_n = path.replace("/", "_").replace("\\", "_")
                            if not clean_n.lower().endswith(ext): clean_n += ext
                            obj_map[clean_n] = obj
                
                for obj in env.objects:
                    ext = get_extension(obj)
                    if ext:
                        name = obj.peek_name()
                        full_name = (name + ext) if name else (str(obj.path_id) + ext)
                        obj_map[full_name] = obj

                for mod_file in os.listdir(mod_data_dir):
                    if mod_file in obj_map:
                        target_obj = obj_map[mod_file]
                        mod_file_path = os.path.join(mod_data_dir, mod_file)
                        
                        with open(mod_file_path, "rb") as f:
                            new_data = f.read()

                        if target_obj.type.name in ["Texture2D", "Sprite"]:
                            data = target_obj.read()
                            data.image = Image.open(mod_file_path)
                            data.save()
                        elif target_obj.type.name == "Font":
                            raw_original = target_obj.get_raw_data()
                            start_pos = find_font_start(raw_original)
                            if start_pos != -1:
                                # Unity原始头 + 修改后的新字体数据
                                final_data = raw_original[:start_pos] + new_data
                                target_obj.set_raw_data(final_data)
                            else:
                                target_obj.set_raw_data(new_data)
                        else:
                            target_obj.set_raw_data(new_data)
                        
                        is_changed = True
                        stats["modified"] += 1
                        print(f"  [√] 成功写回: {mod_file}")

                if is_changed:
                    target_root = os.path.join(output_dir, rel_path)
                    ensure_dir(target_root)
                    with open(os.path.join(target_root, filename), "wb") as f:
                        f.write(env.file.save(packer="lz4" if use_compression else None))
                    stats["repacked"] += 1
            except Exception as e:
                print(f"  [!] 回封失败 {filename}: {e}")
                
    print(f"\n[√] 任务完成！修改资源数: {stats['modified']}")

def main():
    parser = argparse.ArgumentParser(description="UnityFS资源提取工具")
    subparsers = parser.add_subparsers(dest="command", required=True)
    up = subparsers.add_parser("unpack")
    up.add_argument("-i", "--input", default="UnityFS")
    up.add_argument("-o", "--output", default="UnityFS_Extracted")
    re = subparsers.add_parser("repack")
    re.add_argument("-i", "--input", default="UnityFS")
    re.add_argument("-m", "--modded", default="UnityFS_Extracted")
    re.add_argument("-o", "--output", default="UnityFS_Repack")
    re.add_argument("-c", "--compress", action="store_true", default=True)
    args = parser.parse_args()

    if args.command == "unpack":
        unpack_recursive(args.input, args.output)
    elif args.command == "repack":
        repack_recursive(args.input, args.modded, args.output, args.compress)

if __name__ == "__main__":
    main()