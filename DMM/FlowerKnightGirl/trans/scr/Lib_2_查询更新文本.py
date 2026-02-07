import os
import shutil

def find_new_files(new_version_dir, old_versions_parent_dir, output_updata_dir, output_trans_dir):
    """
    比较新版文件夹和所有旧版文件夹，找出新增文件。
    :param new_version_dir: 当前版本分割后的文件夹。
    :param old_versions_parent_dir: 存放所有历史译文文件夹的父目录。
    :param output_updata_dir: 输出新增日文文件的目录。
    :param output_trans_dir: 输出新增待翻译文件的目录。
    """
    print("开始查询更新文件...")
    # 1. 获取所有旧版本的文件名集合
    old_files = set()
    if os.path.exists(old_versions_parent_dir):
        for subdir in os.listdir(old_versions_parent_dir):
            subdir_path = os.path.join(old_versions_parent_dir, subdir)
            if os.path.isdir(subdir_path):
                print(f"扫描历史文件夹: {subdir}")
                for filename in os.listdir(subdir_path):
                    if filename.endswith(".json"):
                        old_files.add(filename)
    print(f"历史文件总数: {len(old_files)}")

    # 2. 获取新版本的文件名集合
    new_files_set = set(os.listdir(new_version_dir))
    print(f"当前版本文件总数: {len(new_files_set)}")

    # 3. 找出差异（新增的文件）
    added_files = new_files_set - old_files
    
    if not added_files:
        print("未找到任何新增文件。")
        return 0

    print(f"发现 {len(added_files)} 个新增文件。")

    # 4. 复制新增文件到输出目录
    os.makedirs(output_updata_dir, exist_ok=True)
    os.makedirs(output_trans_dir, exist_ok=True)

    for file_name in added_files:
        source_path = os.path.join(new_version_dir, file_name)
        if os.path.isfile(source_path):
            shutil.copy(source_path, os.path.join(output_updata_dir, file_name))
            shutil.copy(source_path, os.path.join(output_trans_dir, file_name))
    
    print(f"新增文件已复制到 '{output_updata_dir}' 和 '{output_trans_dir}'")
    return len(added_files)