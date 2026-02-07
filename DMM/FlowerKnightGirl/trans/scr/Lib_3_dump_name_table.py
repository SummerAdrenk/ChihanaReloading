import os
import json
import csv

def dump_names(json_dir, output_csv_path):
    """
    从指定目录的JSON文件中提取人名并生成CSV。
    :param json_dir: 包含JSON文件的目录。
    :param output_csv_path: 输出的CSV文件路径。
    """
    print(f"开始从 '{json_dir}' 提取人名...")
    name_dict = {}

    if not os.path.exists(json_dir):
        print(f"错误：目录 '{json_dir}' 不存在。")
        return

    for filename in os.listdir(json_dir):
        if filename.endswith(".json"):
            with open(os.path.join(json_dir, filename), "r", encoding="utf-8") as f:
                try:
                    data = json.load(f)
                    # 兼容分割后的文件格式 { "c...": [...] }
                    if isinstance(data, dict):
                        # 提取字典里的列表
                        data_list = next(iter(data.values()), [])
                    else:
                        data_list = data
                        
                    for obj in data_list:
                        if "name" in obj and isinstance(obj["name"], str) and obj["name"]:
                            name = obj["name"]
                            name_dict[name] = name_dict.get(name, 0) + 1
                except (json.JSONDecodeError, AttributeError):
                    print(f"警告：读取或解析文件 '{filename}' 失败，已跳过。")
                    continue
    
    if not name_dict:
        print("未提取到任何人名。")
        return

    # 写入CSV
    with open(output_csv_path, "w", encoding="utf-8-sig", newline="") as f:
        writer = csv.writer(f)
        writer.writerow(["JP_Name", "CN_Name", "Count"])
        for name, count in sorted(name_dict.items(), key=lambda item: item[1], reverse=True):
            writer.writerow([name, "", count])

    print(f"人名提取完成，共 {len(name_dict)} 个。文件已保存到 '{output_csv_path}'")