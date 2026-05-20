#include "ArcTXD.h"
#include <fstream>
#include <stdexcept>
#include <algorithm>
#include <cstring>

// 辅助函数: 用于从字节缓冲区中安全地读取一个32位整数
static int32_t ReadInt32(const std::vector<char>& buffer, size_t& offset) {
    if (offset + 4 > buffer.size()) {
        throw std::runtime_error("读取数据时发生越界错误。");
    }
    int32_t value;
    std::memcpy(&value, buffer.data() + offset, sizeof(int32_t));
    offset += 4;
    return value;
}

// 辅助函数: 用于向字节缓冲区写入一个32位整数
static void WriteInt32(std::vector<char>& buffer, int32_t value) {
    char bytes[4];
    std::memcpy(bytes, &value, sizeof(int32_t));
    buffer.insert(buffer.end(), bytes, bytes + sizeof(int32_t));
}


void ArcTXD::ReadFileToBuffer(const std::filesystem::path& path, std::vector<char>& buffer) {
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file) {
        throw std::runtime_error("无法打开文件: " + path.string());
    }

    std::streamsize size = file.tellg();
    file.seekg(0, std::ios::beg);

    if (size < 0) {
        throw std::runtime_error("无法获取文件大小或文件流错误: " + path.string());
    }
    buffer.resize(static_cast<size_t>(size));

    if (!file.read(buffer.data(), size)) {
        throw std::runtime_error("无法读取文件: " + path.string());
    }
}

ArcTXD::ArcTXD(const std::filesystem::path& txd_path, const std::filesystem::path& ptr_path) {
    ReadFileToBuffer(txd_path, txd_buffer_);
    ReadFileToBuffer(ptr_path, ptr_buffer_);
}

const std::vector<TxdEntry>& ArcTXD::Export() {
    entries_.clear();

    if (ptr_buffer_.empty()) {
        return entries_;
    }

    size_t offset = 0;
    const int32_t PTR_MAGIC = 0x20525450; // "PTR "

    int32_t magic = ReadInt32(ptr_buffer_, offset);
    if (magic != PTR_MAGIC) {
        throw std::runtime_error("不支持的 PTR 文件格式。");
    }

    int32_t messageCount = ReadInt32(ptr_buffer_, offset);
    if (messageCount == 0) {
        return entries_;
    }

    // 跳过两个保留字段（各4字节）
    offset += 8;

    for (int32_t i = 0; i < messageCount; ++i) {
        if (offset >= ptr_buffer_.size()) break;

        TxdEntry entry;
        entry.messageID = ReadInt32(ptr_buffer_, offset);
        entry.position = ReadInt32(ptr_buffer_, offset);
        entry.maxlength = ReadInt32(ptr_buffer_, offset);

        if (static_cast<size_t>(entry.position + entry.maxlength) > txd_buffer_.size()) {
            throw std::runtime_error("文件索引错误：文本偏移量或长度超出 TXD 文件范围。");
        }
        entries_.push_back(entry);
    }

    // 根据在 .txd 文件中的位置进行排序
    std::sort(entries_.begin(), entries_.end(), [](const TxdEntry& a, const TxdEntry& b) {
        return a.position < b.position;
        });

    // 从 .txd 缓冲区中读取文本数据
    for (auto& entry : entries_) {
        if (entry.maxlength > 0) {
            entry.text = std::string(txd_buffer_.data() + entry.position, entry.maxlength);
        }
        else {
            entry.text = "";
        }
    }

    return entries_;
}

void ArcTXD::Import(const std::vector<TxdEntry>& new_entries) {
    entries_ = new_entries;
    if (entries_.empty()) return;

    // 重建 TXD 缓冲区
    txd_buffer_.clear();
    int32_t current_position = 0;
    for (auto& entry : entries_) {
        // 更新 position 和 maxlength
        entry.position = current_position;
        entry.maxlength = static_cast<int32_t>(entry.text.length()); // 文本应为 UTF-8

        // 将文本数据追加到新的 TXD 缓冲区
        txd_buffer_.insert(txd_buffer_.end(), entry.text.begin(), entry.text.end());
        current_position += entry.maxlength;
    }

    // 重建 PTR 缓冲区
    ptr_buffer_.clear();
    const int32_t PTR_MAGIC = 0x20525450;

    WriteInt32(ptr_buffer_, PTR_MAGIC);
    WriteInt32(ptr_buffer_, static_cast<int32_t>(entries_.size()));
    WriteInt32(ptr_buffer_, 0); // 保留字段1
    WriteInt32(ptr_buffer_, 0); // 保留字段2

    for (const auto& entry : entries_) {
        WriteInt32(ptr_buffer_, entry.messageID);
        WriteInt32(ptr_buffer_, entry.position);
        WriteInt32(ptr_buffer_, entry.maxlength);
    }
}

void ArcTXD::SaveChanges(const std::filesystem::path& out_txd_path, const std::filesystem::path& out_ptr_path) const {
    std::ofstream txd_file(out_txd_path, std::ios::binary);
    if (!txd_file) {
        throw std::runtime_error("无法创建新的 TXD 文件: " + out_txd_path.string());
    }
    txd_file.write(txd_buffer_.data(), txd_buffer_.size());

    std::ofstream ptr_file(out_ptr_path, std::ios::binary);
    if (!ptr_file) {
        throw std::runtime_error("无法创建新的 PTR 文件: " + out_ptr_path.string());
    }
    ptr_file.write(ptr_buffer_.data(), ptr_buffer_.size());
}

const std::vector<TxdEntry>& ArcTXD::GetEntries() const {
    return entries_;
}