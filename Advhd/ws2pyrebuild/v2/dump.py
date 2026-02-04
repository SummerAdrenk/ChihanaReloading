from Lib import *
from WS2FILE import *
import os
import argparse
import re
import sys
import textwrap
from datetime import datetime

def batch_dump(oriPath, outPath):
    os.makedirs(outPath, exist_ok=True)
    info = StatusInfo()
    warning_logs = []

    def check_text(text, context_type, file_name, offset_val):
        # 将文本转为 JSON 转义格式，查看是否有 \uXXXX，若有则说明存在特殊的符号未处理，需要注意
        json_str = json.dumps(text, ensure_ascii=False)
        if "\\u" in json_str:
            bad_chars = re.findall(r'\\u[0-9a-fA-F]{4}', json_str)
            log_entry = (
                f"文件: {file_name}\n"
                f"位置: @{offset_val} (请在{oriPath}中搜索 @{offset_val} 定位)\n"
                f"类型: {context_type}\n"
                f"包含特殊字符: {', '.join(set(bad_chars))}\n"
                f"原文内容: {text}\n"
                f"{'-'*30}"
            )
            warning_logs.append(log_entry)

    print(f"开始处理... 输入: {oriPath} -> 输出: {outPath}")

    for file in os.listdir(oriPath):
        if not file.endswith(".txt"):
            continue

        #print(f"Processing {file}...")
        out = OriJsonOutput()
        
        compiler = WS2FileCompiler(os.path.join(oriPath, file), "utf-8")
        contents = compiler.commands
        
        for c in contents:
            op = c["op"]
            offset = c.get("ori_offset", "Unknown") # 获取偏移量

            # 提取人名
            if op == "15":
                name = c["args"][0]["value"]
                if name != "" and not name.startswith("%LC"):
                    msg_str = f"文件: {file} | 位置: @{offset} | 警告: 人名格式异常 -> {name}\n{'-'*30}"
                    warning_logs.append(msg_str)
                    print(f"Warning: {name} is not a valid name, skipping.")
                
                name = name.replace("%LC", "")
                out.add_name(name)
                
            # 提取普通对话 (Opcode 14)
            elif op == "14":
                # 这里的 args[2] 对应 Opcode 定义 "itTc" 中的 T (文本)
                if len(c["args"]) > 2:
                    msg = c["args"][2]["value"]

                    check_text(msg, "普通对话 (Op14)", file, offset)

                    # 保留换行符(不保留的话请去除，Advhd支持自动换行)
                    # msg = msg.replace("\\n", "") 

                    if "name" in out.dic and out.dic["name"] == "":
                        del out.dic["name"]
                    out.add_text(msg)
                    # 去除末尾的控制符 %K %P
                    out.dic["message"] = re.sub(r"[%KP]*$", "", out.dic["message"])
                    out.append_dict()

            # 提取选项 (Opcode 0F)
            elif op == "0F":
                for arg in c["args"]:
                    # 只提取类型为 T 的内容
                    if arg["type"] == "T":
                        msg = arg["value"]

                        check_text(msg, "选项 (Op0F)", file, offset)

                        out.add_text(msg)
                        out.append_dict()

        out.save_json(os.path.join(outPath, file + ".json"))
        info.update(out)

    info.output(1)

    # 生成警告报告
    print("\n" + "="*50)
    if len(warning_logs) > 0:
        report_file = "warning_report.txt"
        print(f"检测到 {len(warning_logs)} 处潜在问题！")
        print(f"正在生成报告文件: {report_file} ...")
        
        with open(report_file, "w", encoding="utf-8") as f:
            f.write(f"检测时间: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
            f.write(f"说明: 请在 {oriPath} 内对应的 .txt 文件中搜索 '@偏移量' (如 @12345) 来定位行。\n")
            f.write("注意: 部分 \\u3000 (全角空格) 或 \\u00b7 (中间点) 可能是正常的，请酌情处理。\n\n")
            f.write("\n".join(warning_logs))
        
        print("报告生成完毕，请打开 warning_report.txt 查看详情。")
    else:
        print("未检测到任何包含特殊转义字符的文本...")
    print("="*50)

    print("所有步骤已完成...")

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
    AdvHD WS2 Dumper (.txt -> .json)
    usage: python dump.py [-i INPUT] [-o OUTPUT]
    """)

    parser = argparse.ArgumentParser(
        description=desc_text,
        formatter_class=argparse.RawTextHelpFormatter,
        usage=argparse.SUPPRESS 
    )

    parser.add_argument("-i", "--input", default=None, help="反编译的 .txt 文件输入路径")
    parser.add_argument("-o", "--output", default=None, help="输出路径")
    
    args = parser.parse_args()

    if len(sys.argv) == 1:
        print(desc_text)

    final_input = get_arg(args.input, "请输入 .txt 文件夹路径", "Rio1_dec_dump")
    final_output = get_arg(args.output, "请输入输出路径", "Rio1_dec_dump_json")

    batch_dump(final_input, final_output)

    if len(sys.argv) == 1:
        input("\n按回车键退出...")