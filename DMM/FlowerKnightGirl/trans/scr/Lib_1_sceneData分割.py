import json
import os
import re

def split_scene_data(input_file, output_dir):
    """
    将大型sceneData.json文件按段落分割成小文件。
    :param input_file: 输入的sceneData.json文件路径。
    :param output_dir: 输出分割后文件的目录。
    """
    print(f"正在读取文件: {input_file}")
    if not os.path.exists(input_file):
        print(f"错误：输入文件 '{input_file}' 不存在。")
        return

    os.makedirs(output_dir, exist_ok=True)

    with open(input_file, 'r', encoding='utf-8') as file:
        data = file.read()

    # 正则表达式匹配段落名称，如 "c12345_1"
    pattern = re.compile(r'"(c\d+(?:_\d+)?)"\s*:\s*{', re.DOTALL)
    matches = list(pattern.finditer(data))

    if not matches:
        print("错误：在文件中未找到匹配的段落。请检查JSON格式和正则表达式。")
        return

    print(f"找到 {len(matches)} 个段落，开始分割...")
    for i, match in enumerate(matches):
        segment_name = match.group(1)
        start_index = match.start()
        
        end_index = matches[i+1].start() if i < len(matches) - 1 else len(data) - 1
        
        segment_content = data[start_index:end_index].strip().rstrip(',')

        try:
            segment_json = json.loads('{' + segment_content + '}')
            output_file = os.path.join(output_dir, f"{segment_name}.json")
            with open(output_file, 'w', encoding='utf-8') as outfile:
                json.dump(segment_json, outfile, ensure_ascii=False, indent=4)
        except json.JSONDecodeError:
            print(f"警告：段落 '{segment_name}' 内容不是有效的JSON，已跳过。")
            continue
    
    print(f"分割完成，文件已存入 '{output_dir}'")