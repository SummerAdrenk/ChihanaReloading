import re
import json
import argparse
import os

RE_NAME_TAG = re.compile(r'^◆(C[0-9A-F]{8})◆name◆(.+)$')
RE_MSG_BRANCH = re.compile(r'^◆(C[0-9A-F]{8})◆msg◆(branch\d{2})◆(.+)$')
RE_MSG_NORMAL = re.compile(r'^◆(C[0-9A-F]{8})◆msg◆(.+)$')
RE_CHOICE_TAG = re.compile(r'^◆(B[0-9A-F]{8})◆(.+)$') 
RE_OTHER_TAG = re.compile(r'^◆(A[0-9A-F]{8})◆(.+)$')

def extract(file_path):
    if not os.path.exists(file_path):
        print(f"[ERROR] 找不到文件 {file_path}")
        return

    messages_json = []
    name_dict = {}
    temp_dialogue_name = {}

    with open(file_path, 'r', encoding='utf-8') as f:
        lines = f.readlines()

    for line in lines:
        stripped = line.strip()
        if not stripped.startswith('◆'): continue
        m_name = RE_NAME_TAG.match(stripped)
        if m_name:
            msg_id, name_val = m_name.groups()
            temp_dialogue_name[msg_id] = name_val
            name_dict[name_val] = name_val
            continue
        m_other = RE_OTHER_TAG.match(stripped)
        if m_other:
            _, content = m_other.groups()
            name_dict[content] = content

    for line in lines:
        stripped = line.strip()
        if not stripped.startswith('◆'): continue

        m_choice = RE_CHOICE_TAG.match(stripped)
        if m_choice:
            _, content = m_choice.groups()
            messages_json.append({"message": content.rstrip('\\n')})
            continue

        m_br = RE_MSG_BRANCH.match(stripped)
        m_nm = RE_MSG_NORMAL.match(stripped)
        target = m_br or m_nm
        if target:
            groups = target.groups()
            msg_id = groups[0]
            pure_text = groups[-1].rstrip('\\n')
            
            entry = {}
            if msg_id in temp_dialogue_name:
                entry["name"] = temp_dialogue_name[msg_id]
            entry["message"] = pure_text
            messages_json.append(entry)

    with open('message.txt.json', 'w', encoding='utf-8') as f:
        json.dump(messages_json, f, ensure_ascii=False, indent=2)
    with open('namedict.json', 'w', encoding='utf-8') as f:
        json.dump(name_dict, f, ensure_ascii=False, indent=4)
    print("提取完成! 生成 message.txt.json 和 namedict.json。")

def inject(original_txt, trans_json, name_dict_json):
    if not all(os.path.exists(f) for f in [original_txt, trans_json, name_dict_json]):
        print("[ERROR] 文件缺失。")
        return

    with open(trans_json, 'r', encoding='utf-8') as f:
        trans_data = json.load(f)
    with open(name_dict_json, 'r', encoding='utf-8') as f:
        name_map = json.load(f)

    with open(original_txt, 'r', encoding='utf-8') as f:
        lines = f.readlines()

    new_lines = []
    data_idx = 0

    for line in lines:
        stripped = line.strip()
        if not stripped.startswith('◆'):
            new_lines.append(line)
            continue

        m_name = RE_NAME_TAG.match(stripped)
        if m_name:
            msg_id, raw_name = m_name.groups()
            new_lines.append(f"◆{msg_id}◆name◆{name_map.get(raw_name, raw_name)}\n")
            continue

        m_choice = RE_CHOICE_TAG.match(stripped)
        if m_choice:
            if data_idx < len(trans_data):
                txt = trans_data[data_idx]["message"]
                new_lines.append(f"◆{m_choice.group(1)}◆{txt}\n")
                data_idx += 1
            else:
                new_lines.append(line)
            continue

        m_br = RE_MSG_BRANCH.match(stripped)
        if m_br:
            if data_idx < len(trans_data):
                txt = trans_data[data_idx]["message"]
                new_lines.append(f"◆{m_br.group(1)}◆msg◆{m_br.group(2)}◆{txt}\\n\n")
                data_idx += 1
            else:
                new_lines.append(line)
            continue

        m_nm = RE_MSG_NORMAL.match(stripped)
        if m_nm:
            if data_idx < len(trans_data):
                txt = trans_data[data_idx]["message"]
                new_lines.append(f"◆{m_nm.group(1)}◆msg◆{txt}\\n\n")
                data_idx += 1
            else:
                new_lines.append(line)
            continue

        m_other = RE_OTHER_TAG.match(stripped)
        if m_other:
            prefix, content = m_other.groups()
            new_lines.append(f"◆{prefix}◆{name_map.get(content, content)}\n")
            continue

        new_lines.append(line)

    with open(original_txt, 'w', encoding='utf-8') as f:
        f.writelines(new_lines)
    
    print(f"回注完成! 已将翻译内容覆盖写入至 {original_txt}，请使用 TXT_Rebuild.bat 进行重建。")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("-d", "--dump", action="store_true")
    parser.add_argument("-i", "--inject", action="store_true")
    args = parser.parse_args()
    if args.dump: extract('message.txt')
    elif args.inject: inject('message.txt', 'message.txt.trans.json', 'namedict.json')