import csv
import os

def update_name_table(new_file, old_file, dict_file, output_file):
    """
    比较新旧人名表，并使用项目字典进行翻译，最后返回纯粹的新人名。
    :param new_file: 新生成的人名表。
    :param old_file: 最新的旧人名表。
    :param dict_file: 项目GPT字典.txt 的路径。
    :param output_file: 合并更新后的输出文件。
    :return: 一个只包含纯粹新增人名的行列表（字典格式）。
    """
    print("开始更新人名表...")

    # 1. 读取旧的已翻译人名表数据
    old_data = {}
    if old_file and os.path.exists(old_file):
        print(f"读取历史翻译自: {old_file}")
        with open(old_file, 'r', encoding='utf-8-sig') as f:
            reader = csv.DictReader(f)
            for row in reader:
                if row.get("JP_Name") and row.get("CN_Name"):
                    old_data[row['JP_Name']] = row['CN_Name']
    else:
        print("未找到旧人名表。")

    # 2. 读取项目GPT字典
    gpt_dict = {}
    if os.path.exists(dict_file):
        print(f"读取项目字典自: {dict_file}")
        with open(dict_file, 'r', encoding='utf-8') as f:
            for line in f:
                # 兼容 // 开头的注释行和空行
                if line.strip() and not line.strip().startswith('//'):
                    parts = line.strip().split('\t')
                    if len(parts) >= 2 and parts[0] and parts[1]:
                        gpt_dict[parts[0]] = parts[1]
    else:
        print(f"警告: 未找到项目字典 '{dict_file}'。")

    # 3. 读取新文件，进行两轮翻译（旧表优先，字典其次）
    updated_rows = []
    print(f"读取当前人名自: {new_file}")
    with open(new_file, 'r', encoding='utf-8-sig') as f:
        reader = csv.DictReader(f)
        fieldnames = reader.fieldnames
        for row in reader:
            jp_name = row['JP_Name']
            
            # 优先使用旧人名表的翻译
            if jp_name in old_data:
                row['CN_Name'] = old_data[jp_name]
            # 其次使用项目字典的翻译
            elif jp_name in gpt_dict:
                row['CN_Name'] = gpt_dict[jp_name]
            
            updated_rows.append(row)

    # 4. 写入完整的更新后的人名表 (人名替换表updata.csv)
    with open(output_file, 'w', encoding='utf-8-sig', newline='') as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(updated_rows)
    print(f"完整的更新表已保存到 '{output_file}'")

    # 5. 筛选出纯粹的新增人名（在两轮翻译后，中文名仍然为空的）
    truly_new_names = [row for row in updated_rows if not row.get('CN_Name')]
    
    return truly_new_names