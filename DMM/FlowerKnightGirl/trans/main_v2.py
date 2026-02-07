import os
import shutil
from datetime import datetime
import glob
import csv

from scr import (
    Lib_1_sceneData分割 as s1,
    Lib_2_查询更新文本 as s2,
    Lib_3_dump_name_table as s3,
    Lib_4_CSV人名表更新 as s4,
    Lib_5_sceneData合并 as s5,
    Lib_6_恢复sceneData as s6,
)

# --- 配置区 ---
SRC_JS = "sceneData.js" 
SRC_JSON = "sceneData.json"
# scr内的存档文件夹
SCR_DIR = "scr"
OLD_VERSION_DIR = os.path.join(SCR_DIR, "OldVersion")
OLD_VERSION_TRANS_DIR = os.path.join(SCR_DIR, "OldVersionTrans")
NAME_TABLE_DIR = os.path.join(SCR_DIR, "NameTable")
SCENE_DATA_ARCHIVE_DIR = os.path.join(SCR_DIR, "sceneData")
NAME_TABLE_DICT_DIR = os.path.join(SCR_DIR, "NameTableDict")

# --- 辅助函数 ---
def get_current_date_str():
    """获取格式化的当前日期字符串，例如 '2025.10.16'"""
    return datetime.now().strftime("%Y.%m.%d")

def find_latest_folder_by_prefix(prefix):
    """根据前缀查找根目录下最新的日期文件夹"""
    folders = [d for d in os.listdir('.') if os.path.isdir(d) and d.startswith(prefix)]
    if not folders:
        return None
    return max(folders, key=lambda d: os.path.getmtime(d))

def find_latest_file_in_dir(directory, prefix, suffix):
    """在指定目录中查找最新的文件"""
    search_pattern = os.path.join(directory, f"{prefix}*{suffix}")
    files = glob.glob(search_pattern.replace(os.sep, '/'))
    if not files:
        search_pattern_root = f"{prefix}*{suffix}"
        files = glob.glob(search_pattern_root)
        if not files:
            return None
    return max(files, key=os.path.getctime)

def clear_screen():
    """清空控制台屏幕"""
    os.system('cls' if os.name == 'nt' else 'clear')

# --- 各功能函数 ---
def run_step_0():
    print("\n--- 步骤 0: 转换 sceneData.js 为 sceneData.json ---")
    if not os.path.exists(SRC_JS):
        print(f"错误: 未在当前目录找到源文件 '{SRC_JS}'。")
        return

    print(f"正在读取: {SRC_JS}")
    with open(SRC_JS, 'r', encoding='utf-8') as file:
        content = file.read()

    clean_content = content.replace("sceneData = ", "", 1)
    
    print(f"正在写入标准JSON到: {SRC_JSON}")
    with open(SRC_JSON, 'w', encoding='utf-8') as file:
        file.write(clean_content.strip())
        
    print("转换成功！")

def run_step_1():
    print("\n--- 步骤 1: 分割 sceneData ---")
    if not os.path.exists(SRC_JSON):
        print(f"错误: 未在当前目录找到 '{SRC_JSON}' 文件。请先执行步骤0进行转换。")
        return

    date_str = get_current_date_str()
    output_dir = f"FLOWER KNIGHT GIRL【{date_str}】"
    
    s1.split_scene_data(SRC_JSON, output_dir)

def run_step_2():
    print("\n--- 步骤 2: 查询更新文本 ---")
    new_version_dir = find_latest_folder_by_prefix("FLOWER KNIGHT GIRL【")
    if not new_version_dir:
        print("错误: 未找到由步骤1生成的分割文件夹。请先执行步骤1。")
        return
        
    date_str = get_current_date_str()
    output_updata_dir = f"FLOWER KNIGHT GIRL UPDATA【{date_str}】"
    output_trans_dir = f"FLOWER KNIGHT GIRL UPDATA_TRANS【{date_str}】"

    s2.find_new_files(new_version_dir, OLD_VERSION_TRANS_DIR, output_updata_dir, output_trans_dir)

def run_step_3():
    print("\n--- 步骤 3: 提取人名 ---")
    # 找到最新的UPDATA文件夹
    updata_dir = find_latest_folder_by_prefix("FLOWER KNIGHT GIRL UPDATA【")
    if not updata_dir:
        print("错误: 未找到由步骤2生成的UPDATA文件夹。请先执行步骤2。")
        return

    # 构建指向内部orig文件夹的正确路径
    orig_dir = os.path.join(updata_dir, "orig")
    
    if not os.path.isdir(orig_dir):
        print(f"错误: 在 '{updata_dir}' 文件夹内未找到 'orig' 文件夹。")
        print("请先对UPDATA文件夹执行您的提取工具，并确保在其中生成了 'orig' 文件夹。")
        return

    output_csv = "人名替换表.csv"
    # 调用s3脚本，操作对象是UPDATA文件夹内部的orig文件夹
    s3.dump_names(orig_dir, output_csv)

def run_step_4():
    while True:
        clear_screen()
        print("\n--- 步骤 4: 更新人名表 ---")
        print("A、更新初始人名表 (首次执行)")
        print("B、追加更新人名表 (手动翻译new.csv后执行)")
        print("R、返回主菜单")
        
        choice = input("您的选择是: ").upper()
        
        if choice == 'A':
            # --- 执行原来的流程 A ---
            print("\n--- A: 更新初始人名表 (集成项目字典) ---")
            new_csv = "人名替换表.csv"
            dict_file = "项目GPT字典.txt"
            
            if not os.path.exists(new_csv):
                print(f"错误: 未找到 '{new_csv}'。请先执行步骤3。")
                input("\n按回车键继续...")
                continue
                
            old_csv = find_latest_file_in_dir(NAME_TABLE_DIR, "人名替换表", ".csv")
            output_csv_updata = "人名替换表updata.csv"
            
            truly_new_rows = s4.update_name_table(new_csv, old_csv, dict_file, output_csv_updata)

            if truly_new_rows:
                date_str = get_current_date_str()
                output_csv_new = f"人名替换表【{date_str}】new.csv"
                
                print(f"\n发现 {len(truly_new_rows)} 个纯粹新增人名，列表已保存到 '{output_csv_new}'")
                print(f"请手动翻译 '{output_csv_new}' 文件，完成后再回来选择选项 B。")
                
                with open(output_csv_new, 'w', encoding='utf-8-sig', newline='') as f:
                    fieldnames = ['JP_Name', 'CN_Name', 'Count']
                    writer = csv.DictWriter(f, fieldnames=fieldnames)
                    writer.writeheader()
                    writer.writerows(truly_new_rows)
            else:
                print("\n恭喜！本次无任何新增人名。")
            
            input("\n按回车键返回步骤4菜单...")

        elif choice == 'B':
            # --- 执行新增的流程 B ---
            print("\n--- B: 追加更新人名表 ---")
            date_str = get_current_date_str()
            source_updata_csv = "人名替换表updata.csv"
            new_trans_csv = find_latest_file_in_dir('.', "人名替换表【", "】new.csv")
            
            if not new_trans_csv:
                print("错误: 未找到 '...new.csv' 文件。请先执行选项 A。")
                input("\n按回车键继续...")
                continue
            if not os.path.exists(source_updata_csv):
                 print(f"错误: 未找到 '{source_updata_csv}'。请先执行选项 A。")
                 input("\n按回车键继续...")
                 continue

            output_updata_trans_csv = "人名替换表updata_trans.csv"
            output_new_dict = f"项目GPT字典【{date_str}】.txt"
            
            s4.append_translations(source_updata_csv, new_trans_csv, "项目GPT字典.txt", output_updata_trans_csv, output_new_dict)
            
            # --- 存档逻辑 ---
            print("\n--- 存档操作 ---")
            while True:
                archive_choice = input("是否将最终人名表和新字典归档? (y/n): ").lower()
                if archive_choice in ['y', 'n']:
                    break
            
            if archive_choice == 'y':
                # 归档 人名替换表updata_trans.csv
                source_csv_to_archive = "人名替换表updata_trans.csv"
                if os.path.exists(source_csv_to_archive):
                    archive_name_csv = f"人名替换表updata_trans【{date_str}】.csv"
                    archive_path_csv = os.path.join(NAME_TABLE_DIR, archive_name_csv)
                    shutil.copy(source_csv_to_archive, archive_path_csv)
                    print(f"最终人名表已复制归档到: {archive_path_csv}")
                else:
                    print(f"警告: 未找到 '{source_csv_to_archive}' 进行归档。")
                
                # 归档新字典 (这部分逻辑不变)
                if os.path.exists(output_new_dict):
                    os.makedirs(NAME_TABLE_DICT_DIR, exist_ok=True)
                    archive_path_dict = os.path.join(NAME_TABLE_DICT_DIR, output_new_dict)
                    shutil.copy(output_new_dict, archive_path_dict)
                    print(f"新字典已复制归档到: {archive_path_dict}")
                
                # 归档新字典
                if os.path.exists(output_new_dict):
                    # 确保目标文件夹存在
                    os.makedirs(NAME_TABLE_DICT_DIR, exist_ok=True)
                    archive_path_dict = os.path.join(NAME_TABLE_DICT_DIR, output_new_dict)
                    shutil.copy(output_new_dict, archive_path_dict)
                    print(f"新字典已复制归档到: {archive_path_dict}")

            input("\n按回车键返回步骤4菜单...")

        elif choice == 'R':
            break # 退出循环，返回主菜单
        
        else:
            print("无效输入，请输入 A, B 或 R。")
            input("\n按回车键继续...")

def run_step_5():
    print("\n--- 步骤 5: 合并 sceneData ---")
    trans_dir = find_latest_folder_by_prefix("FLOWER KNIGHT GIRL UPDATA_TRANS【")
    if not trans_dir:
        print("错误: 未找到待翻译的 UPDATA_TRANS 文件夹。请先执行步骤2。")
        return
    if not os.path.exists(SRC_JSON):
        print(f"错误: 未找到用于排序的源文件 '{SRC_JSON}'。")
        return

    print("\n重要提示: 请确保你已经在 '{}' 文件夹内完成了所有新增文本的翻译。".format(trans_dir))
    input("按回车键继续合并...")

    date_str = get_current_date_str()
    output_json = f"sceneData【{date_str}】.json"
    
    all_trans_dirs = [os.path.join(OLD_VERSION_TRANS_DIR, d) for d in os.listdir(OLD_VERSION_TRANS_DIR)]
    all_trans_dirs.append(trans_dir)

    s5.merge_scene_data(SRC_JSON, all_trans_dirs, output_json)

    while True:
        choice = input("\n是否进行归档操作 (移动本次更新文件夹和最终译文)? (y/n): ").lower()
        if choice in ['y', 'n']:
            break
    
    if choice == 'y':
        shutil.copy(output_json, os.path.join(SCENE_DATA_ARCHIVE_DIR, output_json))
        print(f"最终译文已复制到: {SCENE_DATA_ARCHIVE_DIR}")

        updata_dir = find_latest_folder_by_prefix("FLOWER KNIGHT GIRL UPDATA【")
        if updata_dir and os.path.exists(updata_dir):
            shutil.move(updata_dir, os.path.join(OLD_VERSION_DIR, updata_dir))
            print(f"文件夹 '{updata_dir}' 已移动到: {OLD_VERSION_DIR}")
        
        if os.path.exists(trans_dir):
            shutil.move(trans_dir, os.path.join(OLD_VERSION_TRANS_DIR, trans_dir))
            print(f"文件夹 '{trans_dir}' 已移动到: {OLD_VERSION_TRANS_DIR}")

def run_step_6():
    print("\n--- 步骤 6: 恢复 sceneData.js ---")
    latest_json = find_latest_file_in_dir('.', "sceneData【", ".json")
    
    if not latest_json:
        print("错误: 未找到由步骤5生成的最终JSON文件 (例如 'sceneData【日期】.json')。")
        return
    
    output_js = latest_json.replace(".json", ".js")
    s6.restore_js_header(latest_json, output_js)


def main():
    while True:
        clear_screen()
        print("==== FLOWER KNIGHT GIRL 机翻向导 Ver1.0.2 ====")
        print("0、转换 sceneData.js -> sceneData.json (可不用)")
        print("1、分割 sceneData")
        print("2、查询更新文本")
        print("3、提取人名")
        print("4、更新人名表")
        print("5、合并 sceneData")
        print("6、恢复 sceneData.js")
        print("9、退出")
        print("=====================================")
        print("注: 请按顺序执行流程0-6")
        
        choice = input("您的选择是: ")
        
        if choice == '0':
            run_step_0()
        elif choice == '1':
            run_step_1()
        elif choice == '2':
            run_step_2()
        elif choice == '3':
            run_step_3()
        elif choice == '4':
            run_step_4()
        elif choice == '5':
            run_step_5()
        elif choice == '6':
            run_step_6()
        elif choice == '9':
            print("程序已退出。")
            break
        else:
            print("无效输入，请输入 0-6 或 9。")
            
        input("\n按回车键返回主菜单...")

if __name__ == "__main__":
    main()