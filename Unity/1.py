import os
import UnityPy

source_file = "pr01.txt"

def unpack_manual():
    if not os.path.exists(source_file):
        print(f"找不到文件: {source_file}")
        return

    # 加载文件
    try:
        env = UnityPy.load(source_file)
    except Exception as e:
        print(f"读取错误: {e}")
        return
        
    print(f"正在提取: {source_file}")
    
    extract_count = 0
    for obj in env.objects:
        if obj.type.name == "TextAsset":
            reader = obj.reader
            reader.Position = obj.byte_start
            
            try:
                # 读文件名长度 (int32)
                name_len = reader.read_int()
                
                # 读文件名内容
                name_bytes = reader.read_bytes(name_len)
                name = name_bytes.decode('utf-8')
                
                # 对齐 (Unity 要求4字节对齐)
                reader.align_stream()
                
                # 读脚本数据长度 (int32)
                script_size = reader.read_int()
                
                # 读脚本数据
                script_bytes = reader.read_bytes(script_size)
                
                # 导出
                output_name = f"{name}.lua.txt"
                with open(output_name, "wb") as f:
                    f.write(script_bytes)
                
                print(f"提取成功: {output_name}")
                print(f"   └── 大小: {script_size} bytes")
                
                extract_count += 1
                
            except Exception as e:
                print(f"提取 {obj.path_id} 失败: {e}")

    if extract_count > 0:
        print(f"\n全部完成！")
        print("注意：Hex分析显示文件头为 'FF FE'，这通常是 UTF-16 LE 编码。")
        print("     如果 VS Code 打开是乱码，请尝试用 UTF-16 LE 重新打开，或者 Shift-JIS。")

if __name__ == "__main__":
    unpack_manual()