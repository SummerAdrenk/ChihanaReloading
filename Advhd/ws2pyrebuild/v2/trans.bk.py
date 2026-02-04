from Lib import *
from WS2FILE import *
from enc_dec_ws2 import *
import os

oriPath = input("请输入原始解包文本路径 (Rio_dump): ")
transPath = input("请输入已翻译的 JSON 路径 (gt_output): ")
outPath = input("请输入生成的二进制文件输出路径 (release\\rio): ")
namedict_path = input("请输入角色名对照表 json 路径 (namedict.json): ")

os.makedirs(outPath, exist_ok=True)
namedict = open_json(namedict_path)

for file in os.listdir(oriPath):
    try:
        transdatas = open_json(os.path.join(transPath, file + ".json"))
    except FileNotFoundError:
        transdatas = []
    
    ws2f = WS2FileCompiler(os.path.join(oriPath, file), "utf-16-le")

    for c in ws2f.commands:
        if c["op"] == "15":
            name = c["args"][0]["value"]
            name = name.replace("%LC", "")
            if name != "":
                new_name = namedict[name]
                c["args"][0]["value"] = "%LC" + new_name
        elif c["op"] == "14":
            msg = c["args"][2]["value"]
            msg_ = msg.replace("\\n", "")
            msg_ = re.sub(r"[%KP]*$", "", msg_)
            if msg_ != "":
                transdata =  transdatas.pop(0)
                transmsg = transdata["message"]
                transmsg = transmsg + re.search(r"[%KP]*$", msg).group(0)
                c["args"][2]["value"] = transmsg
        elif c["op"] == "0F":
            msg = c["args"][2]["value"]
            transdata = transdatas.pop(0)
            transmsg ="  " + transdata["message"] + "  "
            c["args"][2]["value"] = transmsg
            msg = c["args"][8]["value"]
            transdata = transdatas.pop(0)
            transmsg = "  " + transdata["message"] + "  "
            c["args"][8]["value"] = transmsg
    if len(transdatas) > 0:
        print(f"Warning: {file} has unprocessed translation data: {transdatas}")
    ws2f.preCompile()
    temp_out_file = os.path.join(outPath, file.replace(".txt", ""))
    ws2f.compile(temp_out_file)
    # data = open_file_b(os.path.join(outPath, file.replace(".txt", "")))
    # data = enc(data)
    # with open(os.path.join(outPath, file.replace(".txt", ".ws2")), "wb") as f:
    #     f.write(data)

# 加密步骤
print("正在加密生成的二进制文件...")
for file in os.listdir(outPath):
    file_path = os.path.join(outPath, file)
    data = open_file_b(file_path)
    data = enc(data)
    save_file_b(file_path, data)
print("完成！")