import os
import json
import argparse
import textwrap
import sys
import re
from datetime import datetime

# 异常字符集
# 半角: < > / \ { } [ ] | * ^ % $ # @ `:
# 全角: ＜ ＞ ／ ＼ ｛ ｝ ［ ］ ｜ ＊ ＾ ％ ＄ ＃ ＠ ｀
ABNORMAL_CHARS = r'[<>/\\{}[\]|*^%$#@`:＜＞／＼｛｝［］｜＊＾％＄＃＠｀]'

# 非法控制字符 (排除 \n 和 \r)
ILLEGAL_CONTROL_CHARS = r'[\x00-\x09\x0b-\x1f]'

def check_content(text, filename, index, field, logs):

    if not text:
        return

    errors = []

    # 检测非法标点/特殊符号
    bad_chars = set(re.findall(ABNORMAL_CHARS, text))
    if bad_chars:
        errors.append(f"[非正常标点]: {', '.join(bad_chars)}")

    # 检测非法控制字符
    ctrl_chars = re.findall(ILLEGAL_CONTROL_CHARS, text)
    if ctrl_chars:
        # 将控制字符转换为 \xHH 格式显示
        ctrl_display = [f"\\x{ord(c):02x}" for c in ctrl_chars]
        errors.append(f"[非法控制符]: {', '.join(ctrl_display)}")

    # 检测 Unicode 转义残留
    if "\\u" in text:
        unicode_residue = re.findall(r'\\u[0-9a-fA-F]{4}', text)
        if unicode_residue:
             errors.append(f"[转义符残留]: {', '.join(unicode_residue)}")

    # 生成格式化的日志块
    if errors:
        log_block = (
            f"文件: {filename} | Idx: {index} | 字段: [{field}]\n"
            f"  ├── 发现问题: {' | '.join(errors)}\n"
            f"  └── 原文内容: {text}\n"
            f"{'-'*60}"
        )
        logs.append(log_block)

def batch_check(input_path, output_file):
    print(f"\n>> Command: JSON 内容检测")
    print(f"   输入: {input_path}")
    print(f"   输出: {output_file}")

    if not os.path.exists(input_path):
        print(f"[ERROR] 输入文件夹不存在: {input_path}")
        return

    all_logs = []
    file_count = 0
    issue_count = 0

    files = [f for f in os.listdir(input_path) if f.endswith(".json")]
    
    print("正在扫描...")
    
    for filename in files:
        file_path = os.path.join(input_path, filename)
        try:
            with open(file_path, 'r', encoding='utf-8') as f:
                data = json.load(f)
            
            if not isinstance(data, list):
                print(f"[SKIP] {filename} 格式不是列表")
                continue

            file_count += 1
            
            for index, item in enumerate(data):
                if "name" in item:
                    check_content(item["name"], filename, index, "Name", all_logs)
                if "message" in item:
                    check_content(item["message"], filename, index, "Msg ", all_logs)

        except json.JSONDecodeError:
            print(f"[ERROR] 无法解析 JSON: {filename}")
        except Exception as e:
            print(f"[ERROR] 处理文件 {filename} 时发生异常: {e}")

    issue_count = len(all_logs)

    try:
        with open(output_file, 'w', encoding='utf-8') as f:
            f.write("="*60 + "\n")
            f.write(f">>Testing...\n")
            f.write(f"检测时间: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
            f.write(f"检测路径: {input_path}\n")
            f.write(f"扫描文件: {file_count} 个\n")
            f.write(f"发现问题: {issue_count} 处\n")
            f.write("="*60 + "\n\n")
            
            if issue_count == 0:
                f.write("通过检测...\n")
            else:
                for log in all_logs:
                    f.write(log + "\n")
        
        print("-" * 30)
        if issue_count > 0:
            print(f"检测完成！发现 {issue_count} 处潜在问题。")
            print(f"详情请查看生成的报告: {output_file}")
        else:
            print("检测完成！未发现异常。")
            
    except Exception as e:
        print(f"[ERROR] 写入报告失败: {e}")

def get_arg(value_from_args, prompt_text, default_val):
    if value_from_args is not None:
        return value_from_args
    user_in = input(f"{prompt_text} (默认: {default_val}): ").strip()
    if not user_in:
        return default_val
    return user_in.strip('"')

if __name__ == "__main__":
    desc_text = textwrap.dedent("""\
    ================================================
    AdvHD JSON Checker
    usage: python check_json.py [-i INPUT] [-o OUTPUT]
    """)

    parser = argparse.ArgumentParser(
        description=desc_text,
        formatter_class=argparse.RawTextHelpFormatter,
        usage=argparse.SUPPRESS 
    )

    parser.add_argument("-i", "--input", default=None, help="待检测的 JSON 文件夹路径")
    parser.add_argument("-o", "--output", default=None, help="检测报告输出路径")
    
    args = parser.parse_args()

    if len(sys.argv) == 1:
        print(desc_text)

    # 默认配置
    default_input_path = os.path.join("Rio_dec_dump_json", "orig")
    
    final_input = get_arg(args.input, "请输入 JSON 文件夹路径", default_input_path)
    final_output = get_arg(args.output, "请输入报告输出文件名", "check.txt")

    batch_check(final_input, final_output)

    if len(sys.argv) == 1:
        input("\n按回车键退出...")