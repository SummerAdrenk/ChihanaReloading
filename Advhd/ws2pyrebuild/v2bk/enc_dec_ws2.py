from Lib import *
import os

def dec(data):
    data = bytearray(data)
    for i in range(len(data)):
        data[i] = ((data[i] >> 2) + ((data[i] << 6)) & 0xFF)
    return bytes(data)

def enc(data):
    data = bytearray(data)
    for i in range(len(data)):
        data[i] = ((data[i] << 2) + ((data[i] >> 6)) & 0xFF)
    return bytes(data)

if __name__ == "__main__":
    mode = input("请选择模式: 1. dec 2. enc: ")
    oriPath = input("请输入输入文件夹路径: ")
    outPath = input("请输入输出文件夹路径: ")
    
    os.makedirs(outPath, exist_ok=True)

    for file in os.listdir(oriPath):
        if file.endswith(".ws2"):
            print(f"Processing {file}...")
            with open(os.path.join(oriPath, file), "rb") as f:
                data = f.read()
            
            result_data = dec(data) if mode == "1" else enc(data)
            
            with open(os.path.join(outPath, file), "wb") as f:
                f.write(result_data)
    print("操作完成。")