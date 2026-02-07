import os
import sys
import argparse
try:
    import UnityPy
except ImportError:
    print("[ERROR] 未找到 'UnityPy' 模块。")
    sys.exit(1)

def ensure_dir(path):
    if not os.path.exists(path):
        os.makedirs(path)

def unpack_recursive(input_dir, output_dir):
    print(f"\n{'='*60}")
    print(f">> Command: Unpack")
    print(f"   输入: {input_dir}")
    print(f"   输出: {output_dir}")
    print(f"{'='*60}\n")

    if not os.path.exists(input_dir):
        print(f"[ERROR] 输入路径不存在")
        return

    files_to_process = []
    if os.path.isfile(input_dir):
        files_to_process = [(os.path.dirname(input_dir), [], [os.path.basename(input_dir)])]
    else:
        files_to_process = os.walk(input_dir)

    total_extracted = 0

    for root, _, files in files_to_process:
        target_files = [f for f in files if f.endswith(('.unity3d', '.bytes', '.arc', '.bin')) or 'assetbundle' in f.lower()]
        
        for filename in target_files:
            file_path = os.path.join(root, filename)
            rel_path = os.path.relpath(root, input_dir) if os.path.isdir(input_dir) else ""
            bundle_out_dir = os.path.join(output_dir, rel_path, f"{filename}_dump")
            
            try:
                env = UnityPy.load(file_path)
            except:
                continue

            print(f"正在扫描包: {filename}")
            
            # 获取文件系统列表
            if hasattr(env, "file") and hasattr(env.file, "files"):
                fs_files = env.file.files
            elif hasattr(env, "files"):
                 fs_files = env.files
            else:
                fs_files = {}

            for internal_name, file_reader in fs_files.items():
                clean_name = os.path.basename(internal_name)
                
                # 过滤条件：只要CAB资源文件
                # 忽略SerializedFile (它们通常没有后缀，或者是metadata)
                if "CAB-" in clean_name and (".resource" in clean_name or ".resS" in clean_name):
                    try:
                        data = None
                        
                        # 尝试直接作为 bytes 获取
                        if hasattr(file_reader, "bytes"):
                            data = file_reader.bytes
                        # 尝试读取剩余所有内容 (流式)
                        elif hasattr(file_reader, "read"):
                            try:
                                # 有些流不支持seek，直接读
                                # 如果能seek就重置，不能就算了
                                if hasattr(file_reader, "seek"):
                                    file_reader.seek(0)
                                data = file_reader.read()
                            except:
                                pass
                        # 尝试作为memoryview转换
                        if data is None and hasattr(file_reader, "view"):
                            data = bytes(file_reader.view)

                        # 如果以上都失败，且对象本身可迭代或可转bytes
                        if data is None:
                            try:
                                data = bytes(file_reader)
                            except:
                                pass
                        
                        # 最终检查
                        if not data or len(data) == 0:
                            #print(f"  [SKIP] 空数据: {clean_name}")
                            continue

                        # 智能重命名后缀
                        ext = ".bin"
                        if len(data) > 12:
                            header = data[:12]
                            if header[4:8] == b'ftyp': 
                                ext = ".mp4"
                            elif header.startswith(b'\x1a\x45\xdf\xa3'):
                                ext = ".webm"
                            elif header.startswith(b'OggS'):
                                ext = ".ogg"
                        
                        # 只有当它是视频时才保存
                        if ext in [".mp4", ".webm"]:
                            out_name = clean_name
                            if not out_name.endswith(ext):
                                out_name += ext
                                
                            ensure_dir(bundle_out_dir)
                            with open(os.path.join(bundle_out_dir, out_name), 'wb') as f:
                                f.write(data)
                            
                            print(f"  -> 成功提取: {out_name} (Size: {len(data)/1024/1024:.2f} MB)")
                            total_extracted += 1
                            
                    except Exception as e:
                        # 仅打印非 SerializedFile 错误
                        if "SerializedFile" not in str(type(file_reader)):
                            print(f"  提取错误 {clean_name}: {e}")

    print(f"\n[Total] 总计提取视频文件: {total_extracted}")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument('command', nargs='?', default='unpack')
    parser.add_argument('-i', '--input', default=None)
    parser.add_argument('-o', '--output', default=None)
    args, _ = parser.parse_known_args()

    input_path = args.input
    if not input_path:
         user_in = input("输入文件夹路径 (默认: arc_unpack): ").strip('"')
         input_path = user_in if user_in else "arc_unpack"
    
    output_path = args.output if args.output else "arc_dump"

    unpack_recursive(input_path, output_path)