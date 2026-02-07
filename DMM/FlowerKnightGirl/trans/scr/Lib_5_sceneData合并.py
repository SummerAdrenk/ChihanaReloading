import json
import os
import re

def get_original_order(source_file):
    """从源文件获取段落的原始顺序。"""
    with open(source_file, 'r', encoding='utf-8') as file:
        data = file.read()
    pattern = re.compile(r'"(c\d+(?:_\d+)?)"\s*:', re.DOTALL)
    matches = pattern.findall(data)
    return matches

def merge_scene_data(source_order_file, translated_dirs, output_file):
    """
    将多个文件夹中的小JSON文件按原始顺序合并。
    :param source_order_file: 提供顺序的原始sceneData.json。
    :param translated_dirs: 包含已翻译小JSON文件的目录列表。
    :param output_file: 合并后的输出文件路径。
    """
    print("开始合并所有翻译文件...")
    # 1. 获取原始顺序
    original_order = get_original_order(source_order_file)
    print(f"获取到 {len(original_order)} 个段落的原始顺序。")

    # 2. 构建一个包含所有翻译文件的查找字典
    translation_files = {}
    for directory in translated_dirs:
        if os.path.exists(directory):
            print(f"扫描翻译文件夹: {directory}")
            for filename in os.listdir(directory):
                if filename.endswith(".json"):
                    # 如果有重复文件，后扫描的会覆盖先扫描的（正好让新版覆盖旧版）
                    segment_name = filename.replace(".json", "")
                    translation_files[segment_name] = os.path.join(directory, filename)

    # 3. 按原始顺序合并
    merged_data = {}
    for segment_name in original_order:
        file_path = translation_files.get(segment_name)
        if file_path:
            with open(file_path, 'r', encoding='utf-8') as file:
                data = json.load(file)
                merged_data.update(data)
        else:
            print(f"警告: 在所有翻译文件夹中都未找到 '{segment_name}.json'，此段落将不会包含在最终文件中。")

    # 4. 写入最终文件
    with open(output_file, 'w', encoding='utf-8') as outfile:
        json.dump(merged_data, outfile, ensure_ascii=False, indent=4)

    print(f"所有文件已按原始顺序合并到 '{output_file}'")