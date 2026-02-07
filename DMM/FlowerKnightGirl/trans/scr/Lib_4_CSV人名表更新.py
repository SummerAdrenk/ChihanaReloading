import csv
import os

def update_name_table(new_file, old_file, dict_file, output_file):
    """
    比较新旧人名表，并使用项目字典进行翻译，最后返回纯粹的新人名。
    (此函数功能保持不变)
    """
    print("开始更新人名表...")

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

    gpt_dict = {}
    if os.path.exists(dict_file):
        print(f"读取项目字典自: {dict_file}")
        with open(dict_file, 'r', encoding='utf-8') as f:
            for line in f:
                if line.strip() and not line.strip().startswith('//'):
                    parts = line.strip().split('\t')
                    if len(parts) >= 2 and parts[0] and parts[1]:
                        gpt_dict[parts[0]] = parts[1]
    else:
        print(f"警告: 未找到项目字典 '{dict_file}'。")

    updated_rows = []
    with open(new_file, 'r', encoding='utf-8-sig') as f:
        reader = csv.DictReader(f)
        fieldnames = reader.fieldnames
        for row in reader:
            jp_name = row['JP_Name']
            if jp_name in old_data:
                row['CN_Name'] = old_data[jp_name]
            elif jp_name in gpt_dict:
                row['CN_Name'] = gpt_dict[jp_name]
            updated_rows.append(row)

    with open(output_file, 'w', encoding='utf-8-sig', newline='') as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(updated_rows)
    print(f"完整的更新表已保存到 '{output_file}'")

    truly_new_names = [row for row in updated_rows if not row.get('CN_Name')]
    return truly_new_names

# --- 【新增函数】 ---
def append_translations(source_updata_csv, new_trans_csv, dict_file, output_updata_trans_csv, output_new_dict):
    """
    读取手动翻译的new.csv，填充updata.csv，并更新项目字典。
    :param source_updata_csv: '人名替换表updata.csv'
    :param new_trans_csv: 手动翻译好的 '人名替换表【日期】new.csv'
    :param dict_file: 原始的 '项目GPT字典.txt'
    :param output_updata_trans_csv: 最终成品 '人名替换表updata_trans.csv'
    :param output_new_dict: 新生成的 '项目GPT字典【日期】.txt'
    """
    print("\n开始追加更新人名表...")

    # 1. 读取手动翻译的 new.csv 内容
    new_translations = {}
    if not os.path.exists(new_trans_csv):
        print(f"错误: 未找到手动翻译的文件 '{new_trans_csv}'。")
        return
    with open(new_trans_csv, 'r', encoding='utf-8-sig') as f:
        reader = csv.DictReader(f)
        for row in reader:
            if row.get("JP_Name") and row.get("CN_Name"):
                new_translations[row['JP_Name']] = row['CN_Name']
    print(f"从 '{new_trans_csv}' 读取了 {len(new_translations)} 条新翻译。")

    # 2. 读取 updata.csv，并用新翻译填充
    updated_rows = []
    with open(source_updata_csv, 'r', encoding='utf-8-sig') as f:
        reader = csv.DictReader(f)
        fieldnames = reader.fieldnames
        for row in reader:
            jp_name = row['JP_Name']
            # 如果当前行中文名为空，且在新翻译中能找到，则填充
            if not row.get('CN_Name') and jp_name in new_translations:
                row['CN_Name'] = new_translations[jp_name]
            updated_rows.append(row)
            
    # 3. 写入最终的 updata_trans.csv
    with open(output_updata_trans_csv, 'w', encoding='utf-8-sig', newline='') as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(updated_rows)
    print(f"已生成最终翻译人名表: '{output_updata_trans_csv}'")

    # 4. 读取旧字典并追加新翻译，生成新字典
    with open(dict_file, 'r', encoding='utf-8') as f_in, \
         open(output_new_dict, 'w', encoding='utf-8') as f_out:
        # 先复制旧字典内容
        f_out.write(f_in.read())
        # 在末尾追加新条目
        f_out.write('\n// --- Appended on {} ---\n'.format(os.path.basename(output_new_dict).split('【')[1].split('】')[0]))
        for jp_name, cn_name in new_translations.items():
            # 格式: 源中文[Tab]新中文[Tab]name, girl
            f_out.write(f"{jp_name}\t{cn_name}\tname, girl\n")
    print(f"新翻译已追加到项目字典，并另存为: '{output_new_dict}'")
    print(f"请查看字典进行确认并删除更新时间记录后进行后续步骤")