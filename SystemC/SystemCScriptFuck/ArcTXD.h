#pragma once

#include <iostream>
#include <vector>
#include <string>
#include <cstdint>
#include <filesystem>

// 编码枚举
enum class TextEncoding {
    UTF8,
    ShiftJIS
};

// 用于存储单条文本及其元数据的结构体
struct TxdEntry {
    int32_t messageID;
    int32_t position;
    int32_t maxlength;
    std::string text;
};

class ArcTXD {
public:
    // 构造函数，需要 .txd 和 .ptr 文件的路径
    ArcTXD(const std::filesystem::path& txd_path, const std::filesystem::path& ptr_path);

    // 提取文本，返回一个包含所有文本条目的常量引用
    const std::vector<TxdEntry>& Export();

    // 回注文本，用传入的新条目数据更新内部缓冲区
    void Import(const std::vector<TxdEntry>& new_entries);

    // 将修改后的内部缓冲区保存到新的文件
    void SaveChanges(const std::filesystem::path& out_txd_path, const std::filesystem::path& out_ptr_path) const;

    // 获取当前条目的只读访问权限
    const std::vector<TxdEntry>& GetEntries() const;

private:
    std::vector<char> txd_buffer_; // 存储 .txd 文件内容的字节缓冲区
    std::vector<char> ptr_buffer_; // 存储 .ptr 文件内容的字节缓冲区
    std::vector<TxdEntry> entries_; // 存储解析出的文本条目

    // 辅助函数: 从路径读取文件到字节缓冲区
    static void ReadFileToBuffer(const std::filesystem::path& path, std::vector<char>& buffer);
};