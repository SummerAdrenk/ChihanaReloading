import csv
from collections import defaultdict
import os

def update_name_table(new_file, old_file, output_file):
    """
    比较新旧人名表，继承旧翻译并找出新增人名。
    :param new_file: 新生成的人名表。
    :param old_file: 最新的旧人名表。
    :param output_file: 合并更新后的输出文件。
    :return: 新增人名的集合。
    """
    print("开始更新人名表...")
    # 1. 读取旧文件数据
    old_data = {}
    if old_file and os.path.exists(old_file):
        print(f"读取历史翻译自: {old_file}")
        with open(old_file, 'r', encoding='utf-8-sig') as f:
            reader = csv.DictReader(f)
            for row in reader:
                if row.get("JP_Name") and row.get("CN_Name"):
                    old_data[row['JP_Name']] = row['CN_Name']
    else:
        print("未找到旧人名表，将创建全新的人名表。")

    # 2. 读取新文件并合并
    updated_rows = []
    new_names_set = set()
    print(f"读取当前人名自: {new_file}")
    with open(new_file, 'r', encoding='utf-8-sig') as f:
        reader = csv.DictReader(f)
        fieldnames = reader.fieldnames
        for row in reader:
            jp_name = row['JP_Name']
            new_names_set.add(jp_name)
            # 如果在旧表中找到翻译，则继承
            if jp_name in old_data:
                row['CN_Name'] = old_data[jp_name]
            updated_rows.append(row)

    # 3. 写入输出文件
    with open(output_file, 'w', encoding='utf-8-sig', newline='') as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(updated_rows)

    print(f"更新完成，结果已保存到 '{output_file}'")

    # 4. 返回新增的人名
    added_names = new_names_set - set(old_data.keys())
    return added_names