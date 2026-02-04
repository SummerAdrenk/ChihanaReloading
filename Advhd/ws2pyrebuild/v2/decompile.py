from WS2FILE import *
import os
import argparse
import textwrap
import sys

def batch_decompile(scr_path, out_path):
    print(f"\n>> Command: .ws2 反编译")
    print(f"   输入: {scr_path}")
    print(f"   输出: {out_path}")

    if not os.path.exists(scr_path):
        print(f"[ERROR] 输入文件夹不存在: {scr_path}")
        return

    os.makedirs(out_path, exist_ok=True)
    files = os.listdir(scr_path)
    count = 0

    for file in files:
        if not file.lower().endswith(".ws2"):
            continue
        #print(f"Processing {file}...") 
        try:
            with open(os.path.join(scr_path, file), "rb") as f:
                data = f.read()
            dumper = WS2FileDumper(data)
            output_file = os.path.join(out_path, file + ".txt")
            dumper.dump(output_file)
            
            count += 1
            print(f"  -> 已处理: {file}")
            
        except Exception as e:
            print(f"  [ERROR] 处理 {file} 失败: {e}")

    print(f"\n所有任务完成，共处理 {count} 个文件。")

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
    AdvHD WS2 Decompiler Tool
    usage: python decompile.py [-i INPUT] [-o OUTPUT]
    """)

    parser = argparse.ArgumentParser(
        description=desc_text,
        formatter_class=argparse.RawTextHelpFormatter,
        usage=argparse.SUPPRESS 
    )

    parser.add_argument("-i", "--input", default=None, help="已解密的 .ws2 文件输入路径")
    parser.add_argument("-o", "--output", default=None, help="输出路径")
    
    args = parser.parse_args()

    if len(sys.argv) == 1:
        print(desc_text)

    final_input = get_arg(args.input, "请输入已解密的 .ws2 文件夹路径", "Rio1_dec")
    final_output = get_arg(args.output, "请输入输出路径", "Rio1_dec_dump")

    batch_decompile(final_input, final_output)

    if len(sys.argv) == 1:
        input("\n按回车键退出...")