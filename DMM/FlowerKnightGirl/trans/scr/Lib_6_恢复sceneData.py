import os

def restore_js_header(input_json_path, output_js_path):
    """
    读取一个JSON文件，添加 'sceneData = ' 表头，并另存为 .js 文件。
    :param input_json_path: 输入的 .json 文件路径。
    :param output_js_path: 输出的 .js 文件路径。
    """
    print(f"\n开始恢复JS文件: 从 '{input_json_path}'")
    if not os.path.exists(input_json_path):
        print(f"错误: 输入文件 '{input_json_path}' 不存在。")
        return

    try:
        with open(input_json_path, 'r', encoding='utf-8') as f_in:
            json_content = f_in.read()

        # 在文件内容前添加 'sceneData = '
        js_content = "sceneData = " + json_content

        with open(output_js_path, 'w', encoding='utf-8') as f_out:
            f_out.write(js_content)
        
        print(f"成功生成最终JS文件: '{output_js_path}'")

    except Exception as e:
        print(f"恢复JS文件时发生错误: {e}")