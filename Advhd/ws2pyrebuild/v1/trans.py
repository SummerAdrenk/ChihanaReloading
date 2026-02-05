from Lib import *
from WS2FILE import *
import os
import re
import argparse
import sys
import textwrap

def batch_trans(oriPath, transPath, outPath, namedict_path):
    os.makedirs(outPath, exist_ok=True)
    try:
        namedict = open_json(namedict_path)
    except:
        namedict = {}
        print("未找到 namedict.json，将不进行人名替换。")

    print(f"开始回填文本...")
    print(f"   原文: {oriPath}")
    print(f"   译文: {transPath}")
    print(f"   输出: {outPath}")

    count = 0
    for file in os.listdir(oriPath):
        if not file.endswith(".txt"):
            continue
            
        # 读取译文数据
        json_path = os.path.join(transPath, file + ".json")
        try:
            transdatas = open_json(json_path)
        except FileNotFoundError:
            print(f"警告: 找不到翻译文件 {file}.json，将直接打包原文本。")
            transdatas = []

        ws2f = WS2FileCompiler(os.path.join(oriPath, file), "utf-8")

        # 遍历指令进行回填
        for c in ws2f.commands:
            op = c["op"]
            # 回填人名 (Opcode 15)
            if op == "15":
                if len(c["args"]) > 0:
                    name_arg = c["args"][0]
                    ori_name = name_arg["value"]
                    # 去除标记查找
                    clean_name = ori_name.replace("%LC", "")
                    if clean_name:
                        # 查字典替换，没有则用原名
                        new_name = namedict.get(clean_name, clean_name)
                        # 重新加上 %LC
                        name_arg["value"] = "%LC" + new_name

            # 回填对话 (Opcode 14)
            elif op == "14":
                for arg in c["args"]:
                    # 只处理 T 
                    if arg["type"] == "T":
                        ori_msg = arg["value"]
                        
                        check_val = ori_msg.replace("\\n", "")
                        check_val = re.sub(r"[%KP]*$", "", check_val)
                        
                        if check_val != "":
                            if len(transdatas) > 0:
                                transdata = transdatas.pop(0)
                                transmsg = transdata["message"]
                                
                                # 换行符修复
                                transmsg = transmsg.replace("\n", "\\n")
                                
                                # 控制符补全
                                # 先清理翻译可能自带的尾部控制符
                                transmsg = re.sub(r"[%KP]*$", "", transmsg)
                                # 从原文提取尾部控制符
                                match = re.search(r"[%KP]+$", ori_msg)
                                if match:
                                    transmsg += match.group(0)
                                    
                                arg["value"] = transmsg
                            else:
                                print(f"警告: {file} 翻译条目不足 (Op14, Line {c.get('ori_offset','?')})")
                        
                        # Op14 通常只有一段文本，找到并处理后跳出参数循环
                        break 

            # 回填选项 (Opcode 0F)
            elif op == "0F":
                for arg in c["args"]:
                    # 只处理 T 
                    if arg["type"] == "T":
                        if len(transdatas) > 0:
                            transdata = transdatas.pop(0)
                            transmsg = transdata["message"]
                            # 换行符修复
                            transmsg = transmsg.replace("\n", "\\n")
                            # 选项处理: 前后加空格
                            transmsg = "  " + transmsg.strip() + "  "
                            arg["value"] = transmsg
                        else:
                            print(f"警告: {file} 翻译条目不足 (Op0F, Line {c.get('ori_offset','?')})")

        # 检查是否有未使用的翻译
        if len(transdatas) > 0:
            print(f"严重警告: {file} 处理结束后，仍有 {len(transdatas)} 条翻译未被使用。")
            print(f"这意味着 dump 和 trans 的过滤逻辑不一致，或者翻译文件行数对不上。")

        # 编译与加密
        try:
            ws2f.preCompile()
            temp_out = os.path.join(outPath, file.replace(".txt", "")) # 无后缀的临时文件
            ws2f.compile(temp_out)
            
            # 立即加密
            data = open_file_b(temp_out)
            # 加密
            #data = enc(data) 
            
            # 加上 .ws2 后缀保存
            ws2_out_path = temp_out + ".ws2"
            if temp_out.endswith(".ws2"):
                 ws2_out_path = temp_out

            save_file_b(ws2_out_path, data)
            #print(f"Build: {ws2_out_path}")
            
            if os.path.exists(temp_out) and temp_out != ws2_out_path:
                os.remove(temp_out)
                
            count += 1
                
        except Exception as e:
            print(f"错误: 编译 {file} 失败 - {e}")
            import traceback
            traceback.print_exc()
            
    print(f"\n所有步骤已完成，共处理 {count} 个文件。")

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
    AdvHD WS2 Compiler
    usage: python trans.py [-i ORIG] [-t TRANS] [-o OUTPUT] [-n DICT]
    """)

    parser = argparse.ArgumentParser(
        description=desc_text,
        formatter_class=argparse.RawTextHelpFormatter,
        usage=argparse.SUPPRESS 
    )

    parser.add_argument("-i", "--input", default=None, help="原始反编译文本路径 .txt")
    parser.add_argument("-t", "--trans", default=None, help="已翻译的 JSON 路径")
    parser.add_argument("-o", "--output", default=None, help="生成的 .ws2 文件输出路径")
    parser.add_argument("-n", "--namedict", default=None, help="人名表路径")
    
    args = parser.parse_args()

    if len(sys.argv) == 1:
        print(desc_text)

    final_ori = get_arg(args.input, "请输入原始反编译文本路径", "Rio1_dec_dump")
    final_trans = get_arg(args.trans, "请输入已翻译的 JSON 路径", "Rio1_dec_dump_json_trans")
    final_out = get_arg(args.output, "请输入生成的 .ws2 文件输出路径", "Rio1_release")
    final_dict = get_arg(args.namedict, "请输入人名表路径", "namedict_trans.json")

    batch_trans(final_ori, final_trans, final_out, final_dict)

    if len(sys.argv) == 1:
        input("\n按回车键退出...")

"""
该程序会在回填翻译后对反编译文本进行重新编译，因此翻译内容由译文与反编译文本共同决定。
如若dump.py存在漏提而反编译文本存在，可以直接修改反编译文本进行补充。
"""