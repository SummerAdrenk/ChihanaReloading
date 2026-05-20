#include "ArcTXD.h"
#include <fstream>
#include <stdexcept>
#include <filesystem>
#include <vector>
#include <string>
#include <algorithm>
#include <cctype>
#include <iomanip>
#include <sstream>
#include <map>

#ifdef _WIN32
#include <windows.h>
#else
#error "目前仅支持 Windows 环境构建 (依赖 MultiByteToWideChar)"
#endif

// 辅助函数: 将字符串转换为小写
std::string to_lower(std::string s) {
    std::transform(s.begin(), s.end(), s.begin(),
        [](unsigned char c) { return std::tolower(c); });
    return s;
}

// 获取 CodePage
UINT GetCodePage(std::string encoding_name) {
    encoding_name = to_lower(encoding_name);
    if (encoding_name == "sjis" || encoding_name == "shift-jis" || encoding_name == "shift_jis" || encoding_name == "932") {
        return 932; // Shift-JIS
    }
    if (encoding_name == "gbk" || encoding_name == "gb2312" || encoding_name == "936") {
        return 936; // GBK
    }
    if (encoding_name == "utf-8" || encoding_name == "utf8" || encoding_name == "65001") {
        return CP_UTF8;
    }
    // 默认 UTF-8
    return CP_UTF8;
}

// 任意编码 -> UTF-8
std::string ConvertToUTF8(const std::string& src, UINT src_cp) {
    if (src.empty()) return "";
    if (src_cp == CP_UTF8) return src;

    // MultiByte (Src) -> WideChar (UTF-16)
    int wc_len = MultiByteToWideChar(src_cp, 0, src.c_str(), -1, NULL, 0);
    if (wc_len == 0) return src; // 转换失败返回原串
    std::vector<wchar_t> wc_buf(wc_len);
    MultiByteToWideChar(src_cp, 0, src.c_str(), -1, wc_buf.data(), wc_len);

    // WideChar (UTF-16) -> MultiByte (UTF-8)
    int utf8_len = WideCharToMultiByte(CP_UTF8, 0, wc_buf.data(), -1, NULL, 0, NULL, NULL);
    if (utf8_len == 0) return src;
    std::vector<char> utf8_buf(utf8_len);
    WideCharToMultiByte(CP_UTF8, 0, wc_buf.data(), -1, utf8_buf.data(), utf8_len, NULL, NULL);

    return std::string(utf8_buf.data()); // 包含 null 结尾处理
}

// UTF-8 -> 任意编码
std::string ConvertFromUTF8(const std::string& src, UINT dest_cp) {
    if (src.empty()) return "";
    if (dest_cp == CP_UTF8) return src;

    // MultiByte (UTF-8) -> WideChar (UTF-16)
    int wc_len = MultiByteToWideChar(CP_UTF8, 0, src.c_str(), -1, NULL, 0);
    if (wc_len == 0) return src;
    std::vector<wchar_t> wc_buf(wc_len);
    MultiByteToWideChar(CP_UTF8, 0, src.c_str(), -1, wc_buf.data(), wc_len);

    // WideChar (UTF-16) -> MultiByte (Dest)
    // 注意：非 UTF-8 转换时最后两个参数用于处理无法映射的字符，这里使用默认值
    int dest_len = WideCharToMultiByte(dest_cp, 0, wc_buf.data(), -1, NULL, 0, NULL, NULL);
    if (dest_len == 0) return src;
    std::vector<char> dest_buf(dest_len);
    WideCharToMultiByte(dest_cp, 0, wc_buf.data(), -1, dest_buf.data(), dest_len, NULL, NULL);

    return std::string(dest_buf.data());
}

// ============================================================

void print_usage() {
    std::cout << "\n==================================================================================\n";
    std::cout << "SystemCScriptFuck_Ver1.1.0\n";
    std::cout << "             by ChihanaSonnetia\n\n";
    std::cout << "Tareget:\n";
    std::cout << "  Fuck text from SystemC(Interheart/Candy Soft) Engine's .txd files.\n\n";
    std::cout << "Usage:\n";
    std::cout << "  SystemCScriptFuck.exe <命令> <参数...>\n\n";
    std::cout << "Command:\n";
    std::cout << "-export:\n";
    std::cout << "  SystemCScriptFuck.exe export <input_path> <output_dir> [ReadEncoding]\n";
    std::cout << "#[ReadEncoding]: 可选，指定 .txd 的编码 (如 sjis, gbk)，默认为 utf-8。\n\n";
    std::cout << "-import:\n";
    std::cout << "  SystemCScriptFuck.exe import <original_path> <txt_dir> <output_dir> [ReadEncoding] [WriteEncoding]\n";
    std::cout << "#[ReadEncoding]: 读取原始 .txd 的编码，默认为 utf-8。\n";
    std::cout << "#[WriteEncoding]: 写入新 .txd 的编码，默认为 utf-8。\n\n";
}

// 提取单个文件
void handle_export_file(const std::filesystem::path& txd_path, const std::filesystem::path& out_txt_path, const std::string& read_encoding_str) {
    UINT read_cp = GetCodePage(read_encoding_str);

    auto ptr_path = txd_path;
    ptr_path.replace_extension(".ptr");
    if (!std::filesystem::exists(ptr_path)) {
        ptr_path.replace_extension(".PTR");
        if (!std::filesystem::exists(ptr_path)) {
            throw std::runtime_error("找不到配对的 .ptr/.PTR 文件: " + txd_path.stem().string());
        }
    }

    ArcTXD tool(txd_path, ptr_path);
    const auto& entries = tool.Export();

    std::ofstream out_file(out_txt_path);
    if (!out_file) {
        throw std::runtime_error("无法创建输出文本文件: " + out_txt_path.string());
    }

    out_file << "\xEF\xBB\xBF"; // UTF-8 BOM

    size_t counter = 1;
    for (const auto& entry : entries) {
        std::ostringstream seq_tag;
        seq_tag << "A" << std::setw(8) << std::setfill('0') << counter;
        std::string current_text = ConvertToUTF8(entry.text, read_cp);

        // 去除开头的逗号（SystemC 引擎特性）
        if (!current_text.empty() && current_text.front() == ',') {
            current_text = current_text.substr(1);
        }

        // 分割名字和文本 (使用 UTF-8 字符串字面量查找)
        const std::string separator = u8",「";
        size_t pos = current_text.find(separator);

        if (pos != std::string::npos) {
            std::string name = current_text.substr(0, pos);
            std::string message = u8"「" + current_text.substr(pos + separator.length());
            out_file << u8"◇" << seq_tag.str() << u8"◇" << entry.messageID << u8"◇name◇" << name << "\n";
            out_file << u8"◆" << seq_tag.str() << u8"◆" << entry.messageID << u8"◆name◆" << name << "\n\n";
            out_file << u8"◇" << seq_tag.str() << u8"◇" << entry.messageID << u8"◇msg◇" << message << "\n";
            out_file << u8"◆" << seq_tag.str() << u8"◆" << entry.messageID << u8"◆msg◆" << message << "\n\n";
        }
        else {
            out_file << u8"◇" << seq_tag.str() << u8"◇" << entry.messageID << u8"◇msg◇" << current_text << "\n";
            out_file << u8"◆" << seq_tag.str() << u8"◆" << entry.messageID << u8"◆msg◆" << current_text << "\n\n";
        }

        counter++;
    }

    std::cout << "成功: " << txd_path.filename().string() << " -> " << out_txt_path.filename().string()
        << " [Encoding: " << read_encoding_str << "]\n";
}

// 回注单个文件
void handle_import_file(const std::filesystem::path& orig_txd_path, const std::filesystem::path& in_txt_path, const std::filesystem::path& new_txd_path, const std::string& read_encoding_str, const std::string& write_encoding_str) {
    UINT read_cp = GetCodePage(read_encoding_str);
    UINT write_cp = GetCodePage(write_encoding_str);

    auto orig_ptr_path = orig_txd_path;
    orig_ptr_path.replace_extension(".ptr");
    if (!std::filesystem::exists(orig_ptr_path)) {
        orig_ptr_path.replace_extension(".PTR");
        if (!std::filesystem::exists(orig_ptr_path)) {
            throw std::runtime_error("找不到配对的 .ptr/.PTR 文件: " + orig_txd_path.stem().string());
        }
    }

    ArcTXD tool(orig_txd_path, orig_ptr_path);
    auto entries = tool.Export(); 	 // 获取原始条目结构，后面会修改它的 .text 字段
    for (auto& entry : entries) {
        entry.text = ConvertToUTF8(entry.text, read_cp);
    }

    // 创建一个临时的、干净的存储空间 (UTF-8)
    // 这个map用来存放从 .txt 文件中读取并重建好的文本
    // 键是 messageID，值是重建后的完整文本 (例如 "name,「msg" 或 ",msg")
    std::map<int32_t, std::string> rebuilt_texts;
    // 这个map只用于临时存储name部分
    std::map<int32_t, std::string> pending_names;

    std::ifstream in_file(in_txt_path);
    if (!in_file) {
        throw std::runtime_error("无法打开输入文本文件: " + in_txt_path.string());
    }

    char bom[3];
    in_file.read(bom, 3);
    if (bom[0] != '\xEF' || bom[1] != '\xBB' || bom[2] != '\xBF') {
        in_file.seekg(0);
    }

    // 遍历 .txt 文件，将重建好的文本存入临时map
    std::string line;
    while (std::getline(in_file, line)) {
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }

        if (line.rfind(u8"◆", 0) == 0) {
            std::vector<std::string> parts;
            std::string temp_line = line;
            size_t start = 0;
            const std::string delimiter = u8"◆";
            size_t pos = 0;
            while ((pos = temp_line.find(delimiter, start)) != std::string::npos) {
                parts.push_back(temp_line.substr(start, pos - start));
                start = pos + delimiter.length();
            }
            parts.push_back(temp_line.substr(start));

            if (parts.size() != 5) continue;

            try {
                int32_t id = std::stoi(parts[2]);
                std::string type = parts[3];
                std::string text = parts[4]; // 从 txt 读取的已经是 UTF-8

                if (type == "name") {
                    pending_names[id] = text;
                }
                else if (type == "msg") {
                    // 如果是msg，检查是否有对应的名字
                    if (pending_names.count(id)) {
                        // 有名字，则合并它们
                     // 从 text 中移除可能存在的前导「
                        if (text.rfind(u8"「", 0) == 0) {
                            text = text.substr(std::string(u8"「").length());
                        }
                        // 组合成 "name,「msg" 格式
                        rebuilt_texts[id] = pending_names[id] + u8",「" + text;
                        pending_names.erase(id);	// 用完后删除
                    }
                    else {
                        // 没有名字，这是一个旁白
                     // 组合成 ",msg" 格式
                        rebuilt_texts[id] = "," + text;
                    }
                }
            }
            catch (const std::exception&) {}
        }
    }

    // 更新条目
    // 遍历原始条目列表，用重建好的文本更新它
    size_t updated_count = 0;
    for (auto& entry : entries) {
        // 检查这个条目的 ID 是否在重建好的文本 map 中
        if (rebuilt_texts.count(entry.messageID)) {
            // 如果在，就用新的文本覆盖旧的文本
            entry.text = rebuilt_texts[entry.messageID];
            updated_count++;
        }
        // 如果不在，entry.text 将保持其原始值，不会被修改
    }

    std::cout << "信息: 从 " << in_txt_path.filename().string() << " 中读取并更新了 " << updated_count << " 条文本。\n";

    // 回注并保存
    for (auto& entry : entries) {
        entry.text = ConvertFromUTF8(entry.text, write_cp);
        // Import 函数内部会自动根据 entry.text.length() 重新计算 maxlength 和 position
        // 只需要保证 entry.text 里的二进制数据是正确编码的即可
    }

    auto new_ptr_path = new_txd_path;
    new_ptr_path.replace_extension(".ptr");

    tool.Import(entries);
    tool.SaveChanges(new_txd_path, new_ptr_path);

    std::cout << "成功: " << in_txt_path.filename().string() << " -> " << new_txd_path.filename().string()
        << " [OutEncoding: " << write_encoding_str << "]\n";
}

// 提取整个文件夹
void handle_export_dir(const std::filesystem::path& in_dir, const std::filesystem::path& out_dir, const std::string& read_encoding) {
    if (!std::filesystem::exists(in_dir) || !std::filesystem::is_directory(in_dir)) {
        throw std::runtime_error("输入路径不是一个有效的文件夹: " + in_dir.string());
    }
    std::filesystem::create_directories(out_dir);

    int success_count = 0;
    int fail_count = 0;

    for (const auto& dir_entry : std::filesystem::recursive_directory_iterator(in_dir)) {
        if (to_lower(dir_entry.path().extension().string()) == ".txd") {
            try {
                auto out_txt_path = out_dir / dir_entry.path().filename().replace_extension(".txt");
                handle_export_file(dir_entry.path(), out_txt_path, read_encoding);
                success_count++;
            }
            catch (const std::exception& e) {
                std::cerr << "处理 " << dir_entry.path().filename().string() << " 时发生错误: " << e.what() << "\n";
                fail_count++;
            }
        }
    }
    std::cout << "\n批处理完成。成功: " << success_count << ", 失败: " << fail_count << "。\n";
}

// 回注整个文件夹
void handle_import_dir(const std::filesystem::path& orig_dir, const std::filesystem::path& text_dir, const std::filesystem::path& new_dir, const std::string& read_encoding, const std::string& write_encoding) {
    if (!std::filesystem::is_directory(orig_dir)) throw std::runtime_error("原始路径不是文件夹: " + orig_dir.string());
    if (!std::filesystem::is_directory(text_dir)) throw std::runtime_error("文本路径不是文件夹: " + text_dir.string());
    std::filesystem::create_directories(new_dir);

    int success_count = 0;
    int fail_count = 0;

    for (const auto& dir_entry : std::filesystem::recursive_directory_iterator(text_dir)) {
        if (to_lower(dir_entry.path().extension().string()) == ".txt") {
            try {
                auto base_filename = dir_entry.path().filename().replace_extension("");
                auto orig_txd_path = orig_dir / base_filename;

                if (std::filesystem::exists(orig_dir / base_filename.concat(".txd"))) {
                    orig_txd_path = orig_dir / base_filename;
                }
                else if (std::filesystem::exists(orig_dir / base_filename.replace_extension(".TXD"))) {
                    orig_txd_path = orig_dir / base_filename.replace_extension(".TXD");
                }
                else {
                    std::cerr << "警告: 找不到对应的原始文件 " << base_filename.string() << ".txd/.TXD，跳过。\n";
                    fail_count++;
                    continue;
                }

                auto new_txd_path = new_dir / base_filename.replace_extension(".txd");

                handle_import_file(orig_txd_path, dir_entry.path(), new_txd_path, read_encoding, write_encoding);
                success_count++;
            }
            catch (const std::exception& e) {
                std::cerr << "处理 " << dir_entry.path().filename().string() << " 时发生错误: " << e.what() << "\n";
                fail_count++;
            }
        }
    }
    std::cout << "\n批处理完成。成功: " << success_count << ", 失败: " << fail_count << "。\n";
}

int main(int argc, char* argv[]) {
    // 修正参数判断数量逻辑，因为后面有可选参数，所以至少是 export 的 4个参数 (程序名 cmd path out)
    if (argc < 4) {
        print_usage();
        return 1;
    }

    try {
        std::string command = argv[1];

        if (command == "export") {
            // export <input_path> <output_dir> [ReadEncoding]
            if (argc < 4) { print_usage(); return 1; }

            std::filesystem::path input_path = argv[2];
            std::filesystem::path output_dir = argv[3];
            std::string read_encoding = (argc >= 5) ? argv[4] : "utf-8";

            if (std::filesystem::is_directory(input_path)) {
                handle_export_dir(input_path, output_dir, read_encoding);
            }
            else {
                handle_export_file(input_path, output_dir, read_encoding);
            }
        }
        else if (command == "import") {
            // import <original_path> <txt_dir> <output_dir> [ReadEncoding] [WriteEncoding]
            if (argc < 5) { print_usage(); return 1; }

            std::filesystem::path orig_path = argv[2];
            std::filesystem::path txt_dir = argv[3];
            std::filesystem::path output_dir = argv[4];
            std::string read_encoding = (argc >= 6) ? argv[5] : "utf-8";
            std::string write_encoding = (argc >= 7) ? argv[6] : "utf-8";

            if (std::filesystem::is_directory(orig_path)) {
                handle_import_dir(orig_path, txt_dir, output_dir, read_encoding, write_encoding);
            }
            else {
                handle_import_file(orig_path, txt_dir, output_dir, read_encoding, write_encoding);
            }
        }
        else {
            print_usage();
            return 1;
        }

    }
    catch (const std::exception& e) {
        std::cerr << "\n严重错误: " << e.what() << std::endl;
        return 1;
    }

    return 0;
}