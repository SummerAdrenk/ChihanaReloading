import os
import sys
import argparse
import textwrap
import re
from PIL import Image

try:
    import UnityPy
    from UnityPy.enums import ClassIDType
    from UnityPy.streams import EndianBinaryWriter
except ImportError:
    print("错误: 未找到 'UnityPy' 模块。")
    print("请使用 pip install UnityPy 进行安装。")
    input("按回车键退出...")
    sys.exit(1)

class FolderStats:
    # 用于统计每个文件夹的处理情况
    def __init__(self, folder_name):
        self.folder_name = folder_name
        self.success = 0
        self.failed = 0
        self.skipped = 0
        self.raw_res = 0
        self.details = [] 

    def add_success(self):
        self.success += 1

    def add_fail(self, filename, reason):
        self.failed += 1
        self.details.append(f"[ERROR] {filename}: {reason}")

    def add_skip(self):
        self.skipped += 1
        
    def add_raw(self):
        self.raw_res += 1

    def __str__(self):
        return f"目录: {self.folder_name:<40} | 成功: {self.success:<4} | 原始流: {self.raw_res:<3} | 跳过: {self.skipped:<3} | 失败: {self.failed:<3}"

def draw_progress_bar(current, total, filename="", bar_length=25):
    # 绘制单行进度条
    if total == 0: return
    percent = float(current) * 100 / total
    arrow = '-' * int(percent / 100 * bar_length - 1) + '>'
    spaces = ' ' * (bar_length - len(arrow))
    
    # 显示长度限制100
    display_name = filename
    if len(display_name) > 100:
        display_name = "..." + display_name[-97:] # 只有超过100字才截断
        
    # 格式化对齐长度改为 100，确保覆盖掉上一行较长的文字
    sys.stdout.write(f"\r[{arrow}{spaces}] {percent:.1f}% | {display_name:<100}")
    sys.stdout.flush()

def ensure_dir(path):
    if not os.path.exists(path):
        os.makedirs(path)

def sanitize_filename(name):
    # 防止文件名包含非法字符或路径分隔符导致 Permission Denied
    name = str(name).replace("/", "_").replace("\\", "_")
    return re.sub(r'[^\w\-. ]', '', name)

def parse_datapack_header(reader):
    """
    DataPack 头部解析
    结构: [NameLen] + [Name] + [Align] + [DataLen] + [Data]
    """
    start_pos = reader.Position
    try:
        name_len = reader.read_int()
        if name_len <= 0 or name_len > 1024:
            reader.Position = start_pos
            return None, None

        name_bytes = reader.read_bytes(name_len)
        internal_name = name_bytes.decode('utf-8', errors='ignore')
        reader.align_stream(4)

        data_len = reader.read_int()
        remaining = reader.Length - reader.Position
        if data_len <= 0 or data_len > remaining:
            reader.Position = start_pos
            return None, None

        data = reader.read_bytes(data_len)
        return internal_name, data
    except Exception:
        reader.Position = start_pos
        return None, None

def detect_extension(data, obj_type_name=""):

    #类型检测
    if not data or len(data) < 4: return ".bin"
    header = data[:16]
    
    # 图片
    if header.startswith(b'\x89PNG'): return ".png"
    if header.startswith(b'\xff\xd8\xff'): return ".jpg"
    
    # 视频
    if len(header) >= 8 and header[4:8] == b'ftyp': return ".mp4"
    if header.startswith(b'\x1a\x45\xdf\xa3'): return ".webm"
    
    # 压缩纹理 (ASTC/KTX)
    if header.startswith(b'\xAB\x4B\x54\x58'): return ".ktx"
    if header.startswith(b'\x13\xAB\xA1\x5C'): return ".astc"
    
    # 音频
    if header.startswith(b'OggS'): return ".ogg"
    if header.startswith(b'RIFF') and len(header) >= 12 and header[8:12] == b'WAVE': return ".wav"

    # 回退策略
    if obj_type_name == "TextAsset": return ".txt"
    if obj_type_name == "Texture2D": return ".png"
    if obj_type_name == "Font": return ".ttf"
    if obj_type_name == "AudioClip": return ".ogg"
    
    return ".bin"

# 解包

def unpack_recursive(input_dir, output_dir, extract_audio=False):
    print(f"\n{'='*60}")
    print(f">> Command: Unpack")
    print(f"   输入: {input_dir}")
    print(f"   输出: {output_dir}")
    print(f"   音频 .ogg 提取: {'[开启]' if extract_audio else '[关闭]'}")
    print(f"{'='*60}\n")

    if not os.path.exists(input_dir):
        print(f"[ERROR] 输入路径 '{input_dir}' 不存在...")
        return

    # 预扫描
    print("正在扫描文件列表...", end="", flush=True)
    all_files = []
    for root, _, files in os.walk(input_dir):
        for filename in files:
            if filename.endswith(('.unity3d', '.bytes', '.arc', '.bin')) or 'assetbundle' in filename.lower():
                all_files.append((root, filename))
    
    total_files = len(all_files)
    print(f" 发现 {total_files} 个文件")
    if total_files == 0: return

    stats = {
        "success": 0,
        "skipped": 0,
        "failed": 0,
        "errors": [],       # 记录报错详情
        "skipped_list": []  # 记录略过详情
    }

    target_types = ["TextAsset", "Texture2D", "Sprite", "Font"]
    if extract_audio: target_types.append("AudioClip")

    # 开始处理
    for idx, (root, filename) in enumerate(all_files, 1):
        src_path = os.path.join(root, filename)
        display_path = os.path.relpath(src_path, input_dir)
        
        # 绘制进度条
        draw_progress_bar(idx, total_files, display_path)

        try:
            try:
                env = UnityPy.load(src_path)
            except Exception:
                raise Exception("无法识别的 Unity 文件格式")

            fs_files = {}
            if hasattr(env, "file") and hasattr(env.file, "files"): fs_files = env.file.files
            elif hasattr(env, "files"): fs_files = env.files

            rel_path = os.path.relpath(root, input_dir) if os.path.isdir(input_dir) else ""
            is_single_bundle = (len(env.objects) <= 2 and len(fs_files) <= 1)
            
            if is_single_bundle:
                current_out_dir = os.path.join(output_dir, rel_path)
            else:
                current_out_dir = os.path.join(output_dir, rel_path, f"{filename}_dump")

            extracted_count = 0

            # 提取对象
            for obj in env.objects:
                try:
                    if obj.type.name not in target_types: continue

                    if obj.type.name == "TextAsset":
                        reader = obj.reader
                        reader.Position = obj.byte_start
                        dp_name, dp_data = parse_datapack_header(reader)
                        export_name, export_data, ext = "", None, ".txt"
                        if dp_name and dp_data:
                            export_name, export_data = dp_name, dp_data
                            ext = detect_extension(export_data, "TextAsset")
                        else:
                            data = obj.read()
                            export_name = getattr(data, "name", f"Unnamed_{obj.path_id}")
                            export_data = data.script
                        
                        if export_data:
                            safe_name = sanitize_filename(export_name)
                            save_path = os.path.join(current_out_dir, "Text", f"{safe_name}{ext}")
                            if is_single_bundle: save_path = os.path.join(current_out_dir, f"{safe_name}{ext}")
                            ensure_dir(os.path.dirname(save_path))
                            with open(save_path, "wb") as f: f.write(export_data)
                            extracted_count += 1

                    elif obj.type.name in ["Texture2D", "Sprite"]:
                        data = obj.read()
                        raw_name = getattr(data, "name", f"Unnamed_{obj.path_id}")
                        safe_name = sanitize_filename(raw_name)
                        save_folder = os.path.join(current_out_dir, "Images")
                        if is_single_bundle: save_folder = current_out_dir
                        ensure_dir(save_folder)
                        try:
                            img = data.image
                            img.save(os.path.join(save_folder, f"{safe_name}.png"), "PNG")
                            extracted_count += 1
                        except:
                            if data.m_StreamData and data.m_StreamData.path:
                                res_name = os.path.basename(data.m_StreamData.path)
                                if res_name in fs_files:
                                    res_reader = fs_files[res_name]
                                    raw_data = None
                                    if hasattr(res_reader, "read"): 
                                        if hasattr(res_reader, "seek"): res_reader.seek(0)
                                        raw_data = res_reader.read()
                                    elif hasattr(res_reader, "bytes"): raw_data = res_reader.bytes
                                    else: raw_data = bytes(res_reader)
                                    if raw_data:
                                        with open(os.path.join(save_folder, f"{safe_name}_{obj.path_id}.tex"), "wb") as f:
                                            f.write(raw_data)
                                        extracted_count += 1

                    elif extract_audio and obj.type.name == "AudioClip":
                        data = obj.read()
                        if hasattr(data, "samples") and data.samples:
                            save_folder = os.path.join(current_out_dir, "Audio")
                            if is_single_bundle: save_folder = current_out_dir
                            ensure_dir(save_folder)
                            for s_name, s_data in data.samples.items():
                                with open(os.path.join(save_folder, sanitize_filename(s_name)), "wb") as f:
                                    f.write(s_data)
                            extracted_count += 1
                except: continue

            # 提取 Raw Stream
            for name, reader in fs_files.items():
                clean_name = os.path.basename(name)
                if not ("CAB-" in clean_name or ".resS" in clean_name or ".resource" in clean_name): continue
                try:
                    raw_data = None
                    if hasattr(reader, "read"): 
                        if hasattr(reader, "seek"): reader.seek(0)
                        raw_data = reader.read()
                    elif hasattr(reader, "bytes"): raw_data = reader.bytes
                    else: raw_data = bytes(reader)

                    if raw_data and len(raw_data) > 0:
                        ext = detect_extension(raw_data)
                        if (ext in [".mp4", ".webm"]) or is_single_bundle:
                            out_name = clean_name + (ext if not clean_name.endswith(ext) else "")
                            save_folder = os.path.join(current_out_dir, "Videos")
                            if is_single_bundle: save_folder = current_out_dir
                            ensure_dir(save_folder)
                            with open(os.path.join(save_folder, out_name), "wb") as f: f.write(raw_data)
                            extracted_count += 1
                except: pass

            # 结果判定
            if extracted_count > 0:
                stats["success"] += 1
            else:
                stats["skipped"] += 1
                stats["skipped_list"].append(display_path)

        except Exception as e:
            stats["failed"] += 1
            stats["errors"].append(f"{display_path} -> {str(e)}")

    print(f"\n\n{'='*60}")
    print(f"任务统计报告")
    print(f"{'='*60}")
    print(f"  扫描总数:    {total_files}")
    print(f"  成功解包:    {stats['success']}")
    print(f"  跳过(无资源): {stats['skipped']}")
    print(f"  失败/报错:    {stats['failed']}")
    
    # 打印所有被略过的文件
    if stats["skipped_list"]:
        print(f"\n[跳过文件列表 (无有效资源)]")
        for i, path in enumerate(stats["skipped_list"], 1):
             print(f"  {i}. {path}")

    # 打印所有报错的文件
    if stats["errors"]:
        print(f"\n[报错文件列表]")
        for i, err in enumerate(stats["errors"], 1):
            print(f"  {i}. {err}")
            
    print(f"{'='*60}\n")
    print(f"[Total] 解包完成。")

# 封包

def repack_recursive(input_dir, mod_dir, output_dir):
    print(f"\n{'='*60}")
    print(f">> Command: Repack")
    print(f"   原始: {input_dir}")
    print(f"   修改: {mod_dir}")
    print(f"   输出: {output_dir}")
    print(f"{'='*60}\n")

    if not os.path.exists(input_dir) or not os.path.exists(mod_dir):
        print("[ERROR] 原始文件路径或修改文件路径不存在...")
        return

    print("正在扫描任务...", end="", flush=True)
    all_files = []
    for root, _, files in os.walk(input_dir):
        for filename in files:
            if filename.endswith(('.unity3d', '.bytes', '.arc', '.bin')) or 'assetbundle' in filename.lower():
                all_files.append((root, filename))
    
    total_files = len(all_files)
    print(f" 发现 {total_files} 个原始文件")
    if total_files == 0: return

    stats = { "modified": 0, "unmodified": 0, "failed": 0, "errors": [] }
    modification_log = [] # 日志列表

    for idx, (root, filename) in enumerate(all_files, 1):
        src_path = os.path.join(root, filename)
        rel_path = os.path.relpath(root, input_dir)
        display_path = os.path.relpath(src_path, input_dir)
        
        draw_progress_bar(idx, total_files, display_path)
        
        mod_base_path = os.path.join(mod_dir, rel_path)
        potential_dirs = [os.path.join(mod_base_path, f"{filename}_dump"), mod_base_path]
        
        found_mod_dir = None
        for p in potential_dirs:
            if os.path.exists(p):
                found_mod_dir = p
                break
        
        out_save_dir = os.path.join(output_dir, rel_path)
        ensure_dir(out_save_dir)
        out_save_path = os.path.join(out_save_dir, filename)

        if not found_mod_dir:
            try:
                with open(src_path, "rb") as f_src, open(out_save_path, "wb") as f_dst:
                    f_dst.write(f_src.read())
            except: pass
            continue

        try:
            env = UnityPy.load(src_path)
            modified = False
            fs_files = {}
            if hasattr(env, "file") and hasattr(env.file, "files"): fs_files = env.file.files
            elif hasattr(env, "files"): fs_files = env.files

            mod_files_map = {}
            for m_root, _, m_files in os.walk(found_mod_dir):
                for m_f in m_files:
                    mod_files_map[m_f] = os.path.join(m_root, m_f)

            for obj in env.objects:
                # TextAsset处理 (含DataPack图片)
                if obj.type.name == "TextAsset":
                    reader = obj.reader
                    reader.Position = obj.byte_start
                    dp_name, dp_data = parse_datapack_header(reader) # 获取原始DataPack数据
                    
                    is_wrapped = False
                    target_names = []
                    data = None 
                    
                    if dp_name:
                        # 封装文件 (如 chama0101.bytes)
                        safe_dp_name = sanitize_filename(dp_name)
                        target_names = [safe_dp_name + ".png", safe_dp_name + ".jpg", safe_dp_name + ".txt", safe_dp_name, dp_name + ".png", dp_name]
                        is_wrapped = True
                    else:
                        # 普通TextAsset
                        data = obj.read()
                        raw_name = getattr(data, "name", f"Unnamed_{obj.path_id}")
                        safe_name = sanitize_filename(raw_name)
                        target_names = [f"{safe_name}.png", f"{safe_name}.txt", f"{safe_name}_{obj.path_id}.txt"]

                    replacement_path = None
                    for t_name in target_names:
                        if t_name in mod_files_map:
                            replacement_path = mod_files_map[t_name]
                            break
                    
                    if replacement_path:
                        with open(replacement_path, "rb") as f: new_data = f.read()
                        
                        # 二进制比对: 只有内容不同才进行替换
                        original_payload = dp_data if is_wrapped else (data.script if data else b"")
                        if data is None and not is_wrapped: # 兜底读取
                             tmp_obj = obj.read()
                             original_payload = tmp_obj.script

                        if original_payload == new_data:
                            # 内容完全一致，跳过
                            continue 
                        
                        # 内容不同，执行替换并记录日志
                        log_msg = f"[TextAsset] {display_path} -> 替换: {os.path.basename(replacement_path)}"
                        modification_log.append(log_msg)

                        if is_wrapped:
                            writer = EndianBinaryWriter(endian=reader.endian)
                            n_bytes = dp_name.encode('utf-8')
                            writer.write_int(len(n_bytes))
                            writer.write_bytes(n_bytes)
                            writer.align_stream()
                            writer.write_int(len(new_data))
                            writer.write_bytes(new_data)
                            writer.align_stream()
                            obj.set_raw_data(writer.bytes)
                        else:
                            if data is None: data = obj.read()
                            data.script = new_data
                            data.save()
                        modified = True

                # Texture2D处理
                elif obj.type.name == "Texture2D":
                    data = obj.read()
                    raw_name = getattr(data, "name", f"Unnamed_{obj.path_id}")
                    safe_name = sanitize_filename(raw_name)
                    target_names = [f"{safe_name}.png", f"{safe_name}_{obj.path_id}.png"]
                    
                    replacement_path = None
                    for t_name in target_names:
                        if t_name in mod_files_map:
                            replacement_path = mod_files_map[t_name]
                            break
                    
                    if replacement_path:
                        # Texture2D难以进行无损二进制比对，默认只要存在就替换
                        log_msg = f"[Texture2D] {display_path} -> 替换: {os.path.basename(replacement_path)}"
                        modification_log.append(log_msg)
                        
                        img = Image.open(replacement_path)
                        data.image = img
                        data.save()
                        modified = True

            # Stream/Video处理
            for internal_name in fs_files.keys():
                clean_name = os.path.basename(internal_name)
                candidates = [clean_name, clean_name + ".mp4", clean_name + ".webm", clean_name + ".resS"]
                replacement_path = None
                for cand in candidates:
                    if cand in mod_files_map:
                        replacement_path = mod_files_map[cand]
                        break
                
                if replacement_path:
                    with open(replacement_path, "rb") as f: new_stream_data = f.read()
                    
                    # Stream二进制比对
                    obj_raw = fs_files[internal_name]
                    old_stream_data = b""
                    if hasattr(obj_raw, "data"): old_stream_data = bytes(obj_raw.data)
                    elif hasattr(obj_raw, "script"): old_stream_data = obj_raw.script
                    else: old_stream_data = bytes(obj_raw)

                    if old_stream_data == new_stream_data:
                        continue # 内容一致，跳过

                    log_msg = f"[Stream]    {display_path} -> 替换: {os.path.basename(replacement_path)}"
                    modification_log.append(log_msg)

                    if hasattr(obj_raw, "data"): obj_raw.data = new_stream_data
                    elif hasattr(obj_raw, "script"): obj_raw.script = new_stream_data
                    else: fs_files[internal_name] = new_stream_data
                    if hasattr(obj_raw, "size"): obj_raw.size = len(new_stream_data)
                    modified = True

            # 保存逻辑
            if modified:
                saved_data = None
                try: saved_data = env.file.save(packer="lz4")
                except: 
                    try: saved_data = env.file.save()
                    except Exception as e_raw: raise Exception(f"保存失败: {e_raw}")
                if saved_data:
                    with open(out_save_path, "wb") as f: f.write(saved_data)
                    stats["modified"] += 1
                else: raise Exception("保存数据为空")
            else:
                stats["unmodified"] += 1
                with open(src_path, "rb") as f_src, open(out_save_path, "wb") as f_dst:
                    f_dst.write(f_src.read())

        except Exception as e:
            stats["failed"] += 1
            stats["errors"].append(f"{display_path} -> {str(e)}") 
            try:
                with open(src_path, "rb") as f_src, open(out_save_path, "wb") as f_dst:
                    f_dst.write(f_src.read())
            except: pass

    log_file_path = "UnityFS_fix.log"
    with open(log_file_path, "w", encoding='utf-8') as f_log:
        f_log.write(f"UnityFS Repack Log\n")
        f_log.write(f"============================\n")
        f_log.write(f"实际修改文件数: {len(modification_log)}\n\n")
        
        if not modification_log:
            f_log.write("本次没有检测到任何实质性修改 (文件内容一致或未找到替换文件)。\n")
        else:
            for log_entry in modification_log:
                f_log.write(f"{log_entry}\n")

    print(f"\n\n{'='*60}")
    print(f"任务统计报告")
    print(f"{'='*60}")
    print(f"  扫描总数:      {total_files}")
    print(f"  实际修改:      {stats['modified']} (已更新内容)")
    print(f"  无改动:        {stats['unmodified']} (跳过/未找到)")
    print(f"  失败:          {stats['failed']}")
    print(f"  详细日志:      {log_file_path}")
    
    if stats["errors"]:
        print(f"\n[报错文件列表]")
        for i, err in enumerate(stats["errors"], 1):
            print(f"  {i}. {err}")
    print(f"{'='*60}\n")

def get_input(prompt, default):
    ret = input(f"{prompt} (默认: {default}): ").strip().strip('"')
    return ret if ret else default

def get_bool(prompt):
    return input(f"{prompt} (y/N): ").lower() == 'y'

if __name__ == "__main__":
    desc_text = textwrap.dedent("""\
    ================================================
    UnityFS Tool Ver2.0.0
    usage: python UnityFS.py <command> [-i INPUT] [-o OUTPUT] [options]
    """)

    parser = argparse.ArgumentParser(
        description=desc_text,
        formatter_class=argparse.RawTextHelpFormatter,
        usage=argparse.SUPPRESS 
    )
    
    subparsers = parser.add_subparsers(dest='command', title="Available Commands", metavar="")

    # unpack
    p_unpack = subparsers.add_parser('unpack', help="解包资源")
    p_unpack.add_argument('-i', '--input', help="输入文件夹路径")
    p_unpack.add_argument('-o', '--output', help="输出文件夹路径")
    p_unpack.add_argument('-a', '--audio', action='store_true', help="自动提取音频")

    # repack
    p_repack = subparsers.add_parser('repack', help="回封资源")
    p_repack.add_argument('-i', '--input', help="原始文件路径")
    p_repack.add_argument('-f', '--fix', help="修改文件路径")
    p_repack.add_argument('-o', '--output', help="生成文件路径")

    args = parser.parse_args()

    # 默认路径配置
    DIR_UNPACK = "arc_unpack"
    DIR_DUMP   = "arc_dump"
    DIR_FIX    = "arc_fix"
    DIR_RELEASE= "arc_release"

    # 没有任何命令 -> 直接显示帮助
    if not args.command:
        parser.print_help()
        sys.exit(0)

    # 解包
    if args.command == 'unpack':
        if not args.input:
            print(f">> [Unpack] 请确认参数 (按回车使用默认值，.ogg默认不提取):")
            i_path = get_input("输入文件夹路径", DIR_UNPACK)
            o_path = args.output if args.output else get_input("输出文件夹路径", DIR_DUMP)
            do_audio = args.audio if args.audio else get_bool("是否提取音频(.ogg)?")
        else:
            i_path = args.input
            o_path = args.output if args.output else DIR_DUMP
            do_audio = args.audio

        unpack_recursive(i_path, o_path, do_audio)

    # 回封
    elif args.command == 'repack':
        if not args.input:
            print(f">> [Repack] 请确认参数 (按回车使用默认值):")
            i_path = get_input("原始文件路径", DIR_UNPACK)
            f_path = args.fix if args.fix else get_input("修改文件路径", DIR_FIX)
            o_path = args.output if args.output else get_input("生成文件路径", DIR_RELEASE)
        else:
            i_path = args.input
            f_path = args.fix if args.fix else DIR_FIX
            o_path = args.output if args.output else DIR_RELEASE
        
        repack_recursive(i_path, f_path, o_path)
    
    if len(sys.argv) < 2 or (args.command and not args.input):
        input("\n按回车键退出...")

        '''
        1、值得一提的是，在TextMesh资源中，如果选用的字体生成方式，即Character项为dynamic，
        则对应生成的Texture2D位图则不会有具体的位图生成，具体表现为在UABEA中提取该资源时，提示大小为0。
        因此，本工具不会提取出该类资源的位图数据，而在Unity中生成此类资源时，应该选取Unicode。

        2、另外『』、「」这种东西在Unity里实际显示的时候，可能会存在诸如重叠等异常显示的情况，但有意思的是，
        在游戏内的BACKLOG里却能正常显示，并在返回刷新后，也能正常显示了，amazing……

        3、最后不得不吐槽，TextMesh生成的字体还是太糊了，感觉不如TMP，不过暂时也没有找到更好的解决方案，摆了……
        '''
        '''
        1、使用UnityFS.py、arc_unpack.py、arc_pack.py即可，其他请忽略

        2、目前针对实际修改的逻辑还有点问题，不过懒得改了，就这样吧，反正不要全塞fix文件夹里就行了。
        另外针对VideoClip的回封也还有点问题，不过反正也没这个需求，摆了……ovo
        '''