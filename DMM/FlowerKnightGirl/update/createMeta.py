import sys
import requests
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry
import json
import os
import random
import time
import urllib.request
import urllib.parse
import urllib.error
import re
from concurrent.futures import ThreadPoolExecutor
from bs4 import BeautifulSoup
import argparse
import textwrap
import logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s',
    handlers=[
        logging.StreamHandler(sys.stdout)
    ]
)
logger = logging.getLogger(__name__)

qww = lambda x, y: True if x == y else False

data_path = 'data.json'

# 扫描文件夹获取 ID
def get_scene_ids(target_path):
    scene_ids = []
    # 路径检查: 如果 target_path 不存在，返回空
    if not target_path or not os.path.exists(target_path):
        return scene_ids

    scan_path = target_path
    if os.path.basename(target_path.rstrip(os.sep)) != 'scenes':
        if os.path.exists(os.path.join(target_path, 'scenes')):
            scan_path = os.path.join(target_path, 'scenes')
        elif os.path.exists(os.path.join(target_path, 'public', 'scenes')):
            scan_path = os.path.join(target_path, 'public', 'scenes')
    
    if not os.path.exists(scan_path):
        return scene_ids

    logger.info(f"正在扫描新增角色: {scan_path}")
    for folder_name in os.listdir(scan_path):
        # 必须是文件夹
        if not os.path.isdir(os.path.join(scan_path, folder_name)):
            continue

        if folder_name.startswith("c"):
            try:
                if "_" in folder_name:
                    folder_id = int(folder_name[1:7]) + 300000
                else:
                    folder_id = int(folder_name[1:])
                scene_ids.append(folder_id)
            except ValueError:
                continue
    return scene_ids


def load_data():
    if not os.path.exists(data_path):
        return []
    with open(data_path, 'r', encoding="utf-8") as f:
        data = json.load(f)
    return data.get('charaData', [])

def get_aliases(name):
    name = name.replace('(', ' ').replace(')', ' ')
    aliases = name.split()
    return aliases


def get_eng_aliases(eng_name):
    parts = eng_name.replace('(', '').replace(')', '').split()
    aliases = []
    for i in range(0, len(parts), 2):
        aliases.append(parts[i])
        if i + 1 < len(parts):
            aliases.append(parts[i + 1] + ' ' + parts[i])
            aliases.append(parts[i + 1])
    return aliases


def get_eng_name(eng_name):
    name = eng_name
    if "(" in name:
        parts = name.split("(")
        name = parts[0].strip()
        form = parts[1][:-1].replace("-", "_")
    else:
        form = None
    name = name.upper().replace(" ", "_").replace("-", "_").replace("'", "")
    return name, form

def get_form_name(form):
    if not form:
        return None
    parts = form.split()
    if len(parts) > 1:
        return parts[-1].lower()
    else:
        return form.lower()

# 生成js片段
def generate_js_fragment(new_char_dict, new_scene_dict):
    def dict_to_js_str(d):
        if not d: return ""
        json_str = json.dumps(d, ensure_ascii=False, indent=4)
        content = json_str.strip()[1:-1]
        content = re.sub(r'"([a-zA-Z0-9_]+)":', r'\1:', content)
        content = re.sub(r'"(ARTIST|CV|CHAR|TAG|SCENE)\.(.*?)"', r'\1.\2', content)
        return content.strip()

    char_str = dict_to_js_str(new_char_dict)
    scene_str = dict_to_js_str(new_scene_dict)
    return char_str, scene_str

# 注入
def inject_data(content, var_name, fragment):
    if not fragment: return content
    logger.info(f"正在追加 {var_name} 数据...")
    
    # 寻找结束的大括号
    pattern = re.compile(r'(var ' + var_name + r'\s*=\s*\{[\s\S]*?)(\s*\}(?:;?))')
    match = pattern.search(content)
    
    if match:
        pre_content = match.group(1).rstrip()
        closing = match.group(2)
        
        separator = ",\n"
        if pre_content.strip().endswith('{') or pre_content.strip().endswith(','): 
            separator = "\n"
        
        new_block = pre_content + separator + textwrap.indent(fragment, "    ") + closing
        return content.replace(match.group(0), new_block)
    else:
        logger.warning(f"无法找到 {var_name} 代码块，追加失败。")
        return content

def generate_meta(base_root, update_root):
    # 1. 确定 meta.js 路径 (读取源)
    if os.path.exists(os.path.join(base_root, 'public')):
        input_meta_path = os.path.join(base_root, 'public', 'data', 'scripts', 'data', 'meta.js')
    else:
        input_meta_path = os.path.join(base_root, 'data', 'scripts', 'data', 'meta.js')
    
    # 2. 确定输出路径 (保存到 update_root)
    if update_root:
        if os.path.exists(os.path.join(base_root, 'public')): 
             output_dir = os.path.join(update_root, 'public', 'data', 'scripts', 'data')
        else:
             output_dir = os.path.join(update_root, 'data', 'scripts', 'data')
    else:
        output_dir = os.path.dirname(input_meta_path)

    if not os.path.exists(output_dir):
        os.makedirs(output_dir)
    output_meta_path = os.path.join(output_dir, 'meta.js')

    # 3. 读取原文件
    if not os.path.exists(input_meta_path):
        logger.error(f"[ERROR] 找不到原文件 {input_meta_path}，无法执行追加。")
        return

    logger.info(f"读取原文件: {input_meta_path}")
    with open(input_meta_path, 'r', encoding='utf-8') as f:
        meta_content = f.read()

    # 4. 只扫描 update_root
    if not update_root:
        logger.warning("未提供更新包路径，无法计算新增角色。")
        return

    new_ids = get_scene_ids(update_root)
    if not new_ids:
        logger.info("更新包中没有发现新角色，无需修改 meta.js。")
        return

    logger.info(f"在更新包中发现 {len(new_ids)} 个新角色。")

    chara_data = load_data()
    if not chara_data and not os.path.exists(data_path):
         logger.warning("data.json 不存在或为空。正在尝试下载...")
         getData("https://flowerknight.fandom.com/wiki/List_of_Flower_Knights_by_ID")
         chara_data = load_data()
    
    if not chara_data:
        logger.error("无法加载角色数据，请检查网络或 data.json。")
        return

    wiki_map = {c['id']: c for c in chara_data}
    
    new_char_dict = {}
    new_scene_dict = {}
    matched_count = 0

    # 5. 生成新数据字典
    for char_id in new_ids:
        is_bloom = char_id >= 300000
        wiki_id = char_id - 300000 if is_bloom else char_id
        
        if wiki_id in wiki_map:
            chara = wiki_map[wiki_id]
            matched_count += 1
            
            name = chara['name']
            eng_name, form = get_eng_name(chara['engName'])
            formName = get_form_name(form)
            aliases = get_aliases(chara['name'])
            engAlias = get_eng_aliases(chara['engName'])
            
            # 检测老角色
            search_pattern = r'["\']?' + re.escape(eng_name) + r'["\']?\s*:'
            is_old_character = bool(re.search(search_pattern, meta_content))
            
            if not is_old_character:
                if eng_name not in new_char_dict:
                    new_char_dict[eng_name] = {"base": {}} if not form else {}

                char_entry = { "name": { "eng": chara['engName'], "engAlias": engAlias, "jap": name, "japAlias": aliases } }
                
                if form:
                    if "form" not in new_char_dict[eng_name]:
                         new_char_dict[eng_name]["form"] = {}
                    new_char_dict[eng_name]["form"][formName] = char_entry
                else:
                    new_char_dict[eng_name]["base"] = char_entry
                    new_char_dict[eng_name]["base"].update({"tags": [], "gender": "female", "artist": "ARTIST.IGNORE", "cv": "CV.IGNORE"})

                if "base" not in new_char_dict[eng_name] or not new_char_dict[eng_name]["base"]:
                     if "form" in new_char_dict[eng_name]:
                        base_key = list(new_char_dict[eng_name]["form"].keys())[0]
                        new_char_dict[eng_name]["base"] = new_char_dict[eng_name]["form"][base_key].copy()
                        new_char_dict[eng_name]["base"].update({"tags": [], "gender": "female", "artist": "ARTIST.IGNORE", "cv": "CV.IGNORE"})

            # 生成 SCENE
            real_id_str = str(char_id - 300000) + "_2" if is_bloom else str(char_id)
            scene_key = f'c{real_id_str}'
            
            if f"c{real_id_str}" not in meta_content:
                new_scene_dict[scene_key] = {
                    'character': [f"CHAR.{eng_name}"],
                    'tags': { "female": [], "male": [], "location": [], "misc": [] },
                    'ignoredCharacterTags': []
                }
                if formName:
                    new_scene_dict[scene_key]['form'] = [formName]

    # 6. 注入并保存
    char_frag, scene_frag = generate_js_fragment(new_char_dict, new_scene_dict)
    
    new_content = meta_content
    if matched_count > 0:
        if char_frag:
            new_content = inject_data(new_content, "CHAR", char_frag)
        if scene_frag:
            new_content = inject_data(new_content, "SCENE", scene_frag)
        
    with open(output_meta_path, 'w', encoding="utf-8") as f:
        f.write(new_content)
    
    logger.info(f"生成完毕！新 meta.js 已保存至: {output_meta_path}")
    logger.info(f"本次追加了 {len(new_char_dict)} 个新角色定义，{len(new_scene_dict)} 个新场景。")

def askURL(url):
    headers_list = [
        {
            'user-agent': 'Mozilla/5.0 (iPhone; CPU iPhone OS 13_2_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/13.0.3 Mobile/15E148 Safari/604.1'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 8.0.0; SM-G955U Build/R16NW) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/87.0.4280.141 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 10; SM-G981B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/80.0.3987.162 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (iPad; CPU OS 13_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/87.0.4280.77 Mobile/15E148 Safari/604.1'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 8.0; Pixel 2 Build/OPD3.170816.012) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/102.0.0.0 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/88.0.4324.109 Safari/537.36 CrKey/1.54.248666'
        }, {
            'user-agent': 'Mozilla/5.0 (X11; Linux aarch64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/88.0.4324.188 Safari/537.36 CrKey/1.54.250320'
        }, {
            'user-agent': 'Mozilla/5.0 (BB10; Touch) AppleWebKit/537.10+ (KHTML, like Gecko) Version/10.0.9.2372 Mobile Safari/537.10+'
        }, {
            'user-agent': 'Mozilla/5.0 (PlayBook; U; RIM Tablet OS 2.1.0; en-US) AppleWebKit/536.2+ (KHTML like Gecko) Version/7.2.1.0 Safari/536.2+'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; U; Android 4.3; en-us; SM-N900T Build/JSS15J) AppleWebKit/534.30 (KHTML, like Gecko) Version/4.0 Mobile Safari/534.30'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; U; Android 4.1; en-us; GT-N7100 Build/JRO03C) AppleWebKit/534.30 (KHTML, like Gecko) Version/4.0 Mobile Safari/534.30'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; U; Android 4.0; en-us; GT-I9300 Build/IMM76D) AppleWebKit/534.30 (KHTML, like Gecko) Version/4.0 Mobile Safari/534.30'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 7.0; SM-G950U Build/NRD90M) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/62.0.3202.84 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 8.0.0; SM-G965U Build/R16NW) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/63.0.3239.111 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 8.1.0; SM-T837A) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/70.0.3538.80 Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; U; en-us; KFAPWI Build/JDQ39) AppleWebKit/535.19 (KHTML, like Gecko) Silk/3.13 Safari/535.19 Silk-Accelerated=true'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; U; Android 4.4.2; en-us; LGMS323 Build/KOT49I.MS32310c) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/102.0.0.0 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Windows Phone 10.0; Android 4.2.1; Microsoft; Lumia 550) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/46.0.2486.0 Mobile Safari/537.36 Edge/14.14263'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 6.0.1; Moto G (4)) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/102.0.0.0 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 6.0.1; Nexus 10 Build/MOB31T) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/102.0.0.0 Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 4.4.2; Nexus 4 Build/KOT49H) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/102.0.0.0 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 6.0; Nexus 5 Build/MRA58N) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/102.0.0.0 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 8.0.0; Nexus 5X Build/OPR4.170623.006) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/102.0.0.0 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 7.1.1; Nexus 6 Build/N6F26U) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/102.0.0.0 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 8.0.0; Nexus 6P Build/OPP3.170518.006) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/102.0.0.0 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 6.0.1; Nexus 7 Build/MOB30X) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/102.0.0.0 Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (compatible; MSIE 10.0; Windows Phone 8.0; Trident/6.0; IEMobile/10.0; ARM; Touch; NOKIA; Lumia 520)'
        }, {
            'user-agent': 'Mozilla/5.0 (MeeGo; NokiaN9) AppleWebKit/534.13 (KHTML, like Gecko) NokiaBrowser/8.5.0 Mobile Safari/534.13'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 9; Pixel 3 Build/PQ1A.181105.017.A1) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/66.0.3359.158 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 10; Pixel 4) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/81.0.4044.138 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 11; Pixel 3) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/88.0.4324.181 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 5.0; SM-G900P Build/LRX21T) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/102.0.0.0 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 8.0; Pixel 2 Build/OPD3.170816.012) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/102.0.0.0 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (Linux; Android 8.0.0; Pixel 2 XL Build/OPD1.170816.004) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/102.0.0.0 Mobile Safari/537.36'
        }, {
            'user-agent': 'Mozilla/5.0 (iPhone; CPU iPhone OS 10_3_1 like Mac OS X) AppleWebKit/603.1.30 (KHTML, like Gecko) Version/10.0 Mobile/14E304 Safari/602.1'
        }, {
            'user-agent': 'Mozilla/5.0 (iPhone; CPU iPhone OS 13_2_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/13.0.3 Mobile/15E148 Safari/604.1'
        }, {
            'user-agent': 'Mozilla/5.0 (iPad; CPU OS 11_0 like Mac OS X) AppleWebKit/604.1.34 (KHTML, like Gecko) Version/11.0 Mobile/15A5341f Safari/604.1'
        }, {
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/80.0.3987.149 Safari/537.36'
        }
    ]
    headers = random.choice(headers_list)

    session = requests.Session()
    retry = Retry(total=5, backoff_factor=0.5, status_forcelist=[500, 502, 503, 504])
    session.mount('http://', HTTPAdapter(max_retries=retry))
    session.mount('https://', HTTPAdapter(max_retries=retry))

    try:
        response = session.get(url, headers=headers, timeout=20)
        return response.text
    except Exception as e:
        logger.error(f"Wiki 请求失败: {e}")
        return ""

def getData(baseurl):
    logger.info("\n[正在更新 Wiki 数据...]")
    html = askURL(baseurl)
    if not html:
        return
        
    bs = BeautifulSoup(html, "html.parser")
    table = bs.find('table', class_='wikitable')
    if not table:
        logger.error("[ERROR] 找不到 Wiki 表格")
        return

    tbody = table.find('tbody')
    listTwo = tbody.find_all('tr')[2:] if tbody else table.find_all('tr')[2:]

    idList = []
    logger.info(f"正在解析 {len(listTwo)} 条角色数据...")
    with ThreadPoolExecutor() as executor:
        futures = [executor.submit(process_item, item1) for item1 in listTwo]
        for future in futures:
            res = future.result()
            if res:
                idList.append(res)

    dataObj = {"charaData": idList}
    with open(data_path, 'w', encoding="utf-8") as fp:
        fp.write(json.dumps(dataObj, indent=2, ensure_ascii=False))
    print(f"成功更新 {data_path}，共抓取到 {len(idList)} 个角色。")


def process_item(item1):
    cells = item1.find_all('td')
    if len(cells) < 4:
        return None
    res_dict = {}
    try:
        res_dict['id'] = int(cells[1].get_text(strip=True))
        jap_link = cells[2].find('a')
        res_dict['name'] = jap_link.get_text(strip=True) if jap_link else cells[2].get_text(strip=True)
        eng_link = cells[3].find('a')
        res_dict['engName'] = eng_link.get_text(strip=True) if eng_link else cells[3].get_text(strip=True)
    except (IndexError, ValueError):
        return None
    return res_dict

# 获取参数
def get_arg_input(prompt_text, default_val):
    default_str = f" (默认: {default_val})" if default_val else ""
    user_in = input(f"{prompt_text}{default_str}: ").strip()
    return user_in.strip('"').strip("'") if user_in else default_val

def main():
    baseurl = "https://flowerknight.fandom.com/wiki/List_of_Flower_Knights_by_ID"

    desc_text = textwrap.dedent("""\
    ================================================
    Flower Knight Girl Meta Generator
    """)
    print(desc_text)
    
    # 1. 获取路径
    default_base = os.path.abspath(os.path.join(os.getcwd(), "..", "Flower-knight-girl-Viewer", "package.nw"))
    base_root = get_arg_input("请输入旧 package.nw 路径", default_base)
    
    # 默认寻找最新的 update_xxxx 文件夹
    default_update = None
    update_dir = os.path.join(os.getcwd(), "update")
    if os.path.exists(update_dir):
        subdirs = [os.path.join(update_dir, d) for d in os.listdir(update_dir) if d.startswith("update_")]
        if subdirs:
            subdirs.sort(key=os.path.getmtime, reverse=True)
            default_update = subdirs[0]

    update_root = get_arg_input("请输入更新包路径", default_update)

    # 2. 检查 Wiki 数据缓存
    YOUR_TIME_INTERVAL = 30  # 30天
    time_interval_in_seconds = YOUR_TIME_INTERVAL * 24 * 60 * 60

    if os.path.isfile(data_path):
        file_time = os.path.getmtime(data_path)
        current_time = time.time()
        file_age = current_time - file_time
        if file_age > time_interval_in_seconds:
            getData(baseurl)
    else:
        getData(baseurl)

    # 3. 生成
    if not base_root:
        logger.error("必须指定主程序路径！")
        return
        
    generate_meta(base_root, update_root)
    
    input("按下Enter键退出...")

if __name__ == "__main__":
    main()