import json
import re
import os

def fix_translation_json(input_file):
    if not os.path.exists(input_file):
        print(f"错误: 找不到文件 {input_file}")
        return

    REPLACEMENT_RULES = {
        "・": "·",
        "♪": "傿",
        "©": "僂",
    }

    with open(input_file, 'r', encoding='utf-8') as f:
        data = json.load(f)

    # 匹配省略号后的句号
    re_ellipsis_dot = re.compile(r'(\.{2,}|…+)[。.]')

    count_chars = 0
    count_dots = 0

    for item in data:
        if "message" in item:
            text = item["message"]
            original_text = text

            for old_char, new_char in REPLACEMENT_RULES.items():
                if old_char in text:
                    text = text.replace(old_char, new_char)
            
            if text != original_text:
                count_chars += 1
            
            # 删去省略号后的句号
            new_text = re_ellipsis_dot.sub(r'\1', text)
            if new_text != text:
                text = new_text
                count_dots += 1
            
            item["message"] = text

    with open(input_file, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)

    print(f"处理完成并已覆盖原文件: {input_file}")
    print(f"- 修正了 {count_chars} 条包含常规替换字符的文本")
    print(f"- 修正了 {count_dots} 条包含省略号后多余句号的文本")

if __name__ == "__main__":
    fix_translation_json('message.txt.trans.json')