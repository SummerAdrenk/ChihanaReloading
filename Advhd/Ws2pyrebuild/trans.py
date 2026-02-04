from Lib import *
from WS2FILE import *
from enc_dec_ws2 import *
import os
import re

# 路径配置
oriPath = input("请输入原始解包文本路径 (默认 Rio1_dec_dump): ") or "Rio1_dec_dump"
transPath = input("请输入已翻译的 JSON 路径 (默认 Rio1_dec_dump_txt_trans): ") or "Rio1_dec_dump_txt_trans"
outPath = input("请输入生成的二进制文件输出路径 (默认 Rio1_release): ") or "Rio1_release"
namedict_path = input("请输入角色名对照表 json 路径 (默认 namedict_trans.json): ") or "namedict_trans.json"

os.makedirs(outPath, exist_ok=True)
try:
    namedict = open_json(namedict_path)
except:
    namedict = {}
    print("未找到 namedict.json，将不进行人名替换。")

print("开始回填文本...")

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

    ws2f = WS2FileCompiler(os.path.join(oriPath, file), "utf-16-le")

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
        data = enc(data)
        
        # 加上 .ws2 后缀保存
        ws2_out_path = temp_out + ".ws2"
        # 防止路径出现 .ws2.ws2
        if temp_out.endswith(".ws2"):
             ws2_out_path = temp_out

        save_file_b(ws2_out_path, data)
        # print(f"Build: {ws2_out_path}")
        
        # 删除中间临时文件
        if os.path.exists(temp_out) and temp_out != ws2_out_path:
            os.remove(temp_out)
            
    except Exception as e:
        print(f"错误: 编译 {file} 失败 - {e}")
        import traceback
        traceback.print_exc()

print("所有步骤已完成。")