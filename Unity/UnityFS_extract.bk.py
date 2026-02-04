import os
import UnityPy
from UnityPy.enums import ClassIDType
import sys

def extract_assets(src_file, dst_root):
    """
    从 Unity AssetBundle 文件中提取资源
    """
    # 尝试加载文件
    try:
        env = UnityPy.load(src_file)
    except Exception as e:
        # 如果不是 Unity 文件，或者加载失败，直接跳过
        print(f"  [跳过] 无法加载: {e}")
        return

    # 创建该文件对应的输出文件夹
    # 例如: .../day01m.txt -> .../extracted_assets/day01m/
    relative_path = os.path.relpath(src_file, start=input_base_dir)
    file_output_dir = os.path.join(dst_root, relative_path + "_dump")
    
    # 标记是否有资源被提取
    extracted_count = 0

    # 遍历所有对象
    for obj in env.objects:
        # 获取对象类型
        if obj.type in [ClassIDType.AssetBundle, ClassIDType.GameObject, ClassIDType.Transform]:
            # 跳过非内容资源
            continue

        # 解析数据
        try:
            data = obj.read()
        except Exception as e:
            print(f"  [错误] 读取对象失败: {e}")
            continue

        # 确定输出子目录（按类型分类）
        type_str = str(obj.type)
        obj_dir = os.path.join(file_output_dir, type_str)
        
        # 获取资源名称
        if hasattr(data, "name") and data.name:
            name = data.name
        else:
            name = f"Unnamed_{obj.path_id}"

        # 过滤文件名中的非法字符
        name = "".join(c for c in name if c.isalnum() or c in (' ', '.', '_', '-'))

        # --- 根据类型进行提取 ---

        # 1. 图片 (Texture2D, Sprite)
        if obj.type == ClassIDType.Texture2D or obj.type == ClassIDType.Sprite:
            try:
                # 获取图片对象 (Pillow Image)
                image = data.image
                if image:
                    os.makedirs(obj_dir, exist_ok=True)
                    output_path = os.path.join(obj_dir, f"{name}.png")
                    image.save(output_path)
                    print(f"  [图片] {name}.png")
                    extracted_count += 1
            except Exception as e:
                print(f"  [警告] 图片提取失败 {name}: {e}")

        # 2. 文本 (TextAsset)
        elif obj.type == ClassIDType.TextAsset:
            if hasattr(data, "script"):
                os.makedirs(obj_dir, exist_ok=True)
                # TextAsset 通常可能是纯文本或二进制，这里存为原始文件
                # 尝试判断是否为 txt
                ext = ".txt"
                output_path = os.path.join(obj_dir, f"{name}{ext}")
                with open(output_path, "wb") as f:
                    f.write(data.script)
                print(f"  [文本] {name}{ext}")
                extracted_count += 1

        # 3. 字体 (Font)
        elif obj.type == ClassIDType.Font:
            if hasattr(data, "m_FontData") and data.m_FontData:
                os.makedirs(obj_dir, exist_ok=True)
                output_path = os.path.join(obj_dir, f"{name}.ttf") # 假定是ttf，也可能是otf
                with open(output_path, "wb") as f:
                    f.write(data.m_FontData)
                print(f"  [字体] {name}.ttf")
                extracted_count += 1

        # 4. 音频 (AudioClip)
        elif obj.type == ClassIDType.AudioClip:
            # UnityPy 处理音频比较复杂，尝试提取样本
            if hasattr(data, "samples") and data.samples:
                os.makedirs(obj_dir, exist_ok=True)
                for sample_name, sample_data in data.samples.items():
                    # sample_name 通常包含扩展名
                    out_name = sample_name if sample_name else f"{name}.wav"
                    output_path = os.path.join(obj_dir, out_name)
                    with open(output_path, "wb") as f:
                        f.write(sample_data)
                    print(f"  [音频] {out_name}")
                    extracted_count += 1
            # 备用：有时候音频作为 Raw Data 存在 m_AudioData
            elif hasattr(data, "m_AudioData") and data.m_AudioData:
                 os.makedirs(obj_dir, exist_ok=True)
                 output_path = os.path.join(obj_dir, f"{name}.raw_audio")
                 with open(output_path, "wb") as f:
                     f.write(data.m_AudioData)
                 print(f"  [音频-Raw] {name}.raw_audio")
                 extracted_count += 1

        # 5. 视频 (VideoClip)
        # 注意: VideoClip 的数据通常很大，有时不直接存储在 Bundle 中，而是引用外部路径
        elif obj.type == ClassIDType.VideoClip:
            # 检查是否有外部资源引用
            saved = False
            # 尝试查找原始数据，如果 UnityPy 版本支持导出 Video 数据
            # 目前 UnityPy 对 VideoClip 的支持有限，我们尝试转储所有可能的二进制字段
            # 很多时候，视频在 UnityFS 中被存为 TextAsset (bytes) 或者是 StreamedResource
            print(f"  [提示] 发现 VideoClip: {name}，尝试寻找数据...")
            
            pass 

        # 6. Shader (着色器 - 可选)
        elif obj.type == ClassIDType.Shader:
            os.makedirs(obj_dir, exist_ok=True)
            output_path = os.path.join(obj_dir, f"{name}.txt")
            # Shader 解析后的文本
            try:
                with open(output_path, "w", encoding="utf-8") as f:
                    f.write(data.export())
                extracted_count += 1
            except:
                pass

    if extracted_count == 0:
        # 如果没提取到东西，可能是空包或者不支持的类型，删除空文件夹清理
        if os.path.exists(file_output_dir) and not os.listdir(file_output_dir):
            os.rmdir(file_output_dir)
        # print(f"  [信息] 文件中未发现支持导出的资源。")

def process_directory(input_dir, output_dir):
    """
    递归遍历目录并处理文件
    """
    global input_base_dir
    input_base_dir = input_dir

    print(f"开始扫描目录: {input_dir}")
    print(f"输出目录: {output_dir}")

    for root, dirs, files in os.walk(input_dir):
        for file in files:
            file_path = os.path.join(root, file)
            
            # 简单的文件头检查 (UnityFS)
            with open(file_path, 'rb') as f:
                header = f.read(7)
            
            if header == b'UnityFS':
                print(f"\n正在处理 UnityFS 包: {file}")
                extract_assets(file_path, output_dir)
            else:
                # 如果不是 UnityFS，也许是 WebBundle 或者 Raw 资源，暂时跳过
                pass

def main():
    if len(sys.argv) < 2:
        print("用法: python unity_extract.py <解包后的目录> [输出目录]")
        print("示例: python unity_extract.py ./unpacked_data ./final_assets")
        return

    input_dir = sys.argv[1]
    if len(sys.argv) > 2:
        output_dir = sys.argv[2]
    else:
        output_dir = input_dir + "_extracted"

    if not os.path.exists(input_dir):
        print(f"错误: 目录 '{input_dir}' 不存在")
        return

    process_directory(input_dir, output_dir)
    print(f"\n全部完成！文件已保存在: {output_dir}")

if __name__ == "__main__":
    main()