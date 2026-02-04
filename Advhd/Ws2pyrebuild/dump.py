from Lib import *
from WS2FILE import *
import os

# 路径
oriPath = input("请输入反编译后的 .txt 文件夹路径 (默认 Rio1_dec_dump): ") or "Rio1_dec_dump"
outPath = input("请输入生成的翻译用 JSON 存放路径 (默认 Rio1_dec_dump_txt): ") or "Rio1_dec_dump_txt"

os.makedirs(outPath, exist_ok=True)
info = StatusInfo()

for file in os.listdir(oriPath):
    # 过滤非txt文件
    if not file.endswith(".txt"):
        continue

    print(f"Processing {file}...")
    out = OriJsonOutput()
    
    # 读取并解析文件
    compiler = WS2FileCompiler(os.path.join(oriPath, file), "utf-8")
    contents = compiler.commands
    
    for c in contents:
        op = c["op"]
        
        # 提取人名
        if op == "15":
            name = c["args"][0]["value"]
            if name != "" and not name.startswith("%LC"):
                print(f"Warning: {name} is not a valid name, skipping.")
            name = name.replace("%LC", "")
            out.add_name(name)
            
        # 提取普通对话 (Opcode 14)
        elif op == "14":
            # 这里的 args[2] 对应 Opcode 定义 "itTc" 中的 T (文本)
            if len(c["args"]) > 2:
                msg = c["args"][2]["value"]
                # 检测逻辑: 将文本转为 JSON 字符串 (不转义 ASCII)，如果里面还包含 "\\u"，
                # 说明存在无法直接显示的控制字符 (如 \u000b)
                debug_str = json.dumps(msg, ensure_ascii=False)
                if "\\u" in debug_str:
                    # 获取行号方便定位
                    line_num = c.get("ori_offset", "Unknown")
                    print(f"[特殊符号检测] 文件: {file} | Offset: {line_num}")
                    print(f"内容: {debug_str}")
                # 保留换行符
                # msg = msg.replace("\\n", "") 
                if "name" in out.dic and out.dic["name"] == "":
                    del out.dic["name"]
                out.add_text(msg)
                # 去除末尾的控制符 %K %P
                out.dic["message"] = re.sub(r"[%KP]*$", "", out.dic["message"])
                out.append_dict()

        # 提取选项 (Opcode 0F)
        elif op == "0F":
            # 原代码只提取了 args[2] 和 args[8]，这里改为遍历所有参数
            for arg in c["args"]:
                # 只提取类型为 T 的内容
                if arg["type"] == "T":
                    msg = arg["value"]
                    # 检测特殊符号
                    debug_str = json.dumps(msg, ensure_ascii=False)
                    if "\\u" in debug_str:
                        line_num = c.get("ori_offset", "Unknown")
                        print(f"[特殊符号检测] 文件: {file} | Offset: {line_num}")
                        print(f"内容: {debug_str}")
                    out.add_text(msg)
                    out.append_dict() # 每个选项存为一个独立的条目

    out.save_json(os.path.join(outPath, file + ".json"))
    info.update(out)

info.output(1)

print("所有步骤已完成。")