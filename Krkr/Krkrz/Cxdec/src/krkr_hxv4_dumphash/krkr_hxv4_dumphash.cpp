/**
 *  dynamicly compute dirhash and filehash in list
 *   v0.1.1, developed by devseed
 * 
 * build:
 *   clang++ -m32 -shared -Wno-null-dereference -Isrc/compat -DUSECOMPAT src/krkr_hxv4_dumphash.cpp src/compat/tp_stub.cpp src/compat/winversion_v100.def -o asset/build/version.dll -g -gcodeview -Wl,--pdb=asset/build/version.pdb 
 * 
 * usage:
 *   renmae the target dll to version.dll and put into game directory, 
 *   then it will decodes all the lines as name in files.txt and dirs.txt
 *   files.txt -> files_match.txt, dirs.txt -> dirs_match.txt (must be utf16lebom)
 *   after that you can restore content path extracted by KrkrExtractForCxdecV2
 * 
 * tested games:
 *   D.C.5 Plus Happiness ～ダ・カーポ5～プラスハピネス
 * 
 * refer: 
 *   https://github.com/YeLikesss/KrkrExtractForCxdecV2/blob/main/CxdecStringDumper/HashCore.cpp
 */

#include <cstdio>
#include <cstdint>
#include <clocale>
#include <functional>
#include <string>
#include <vector>
#include <windows.h>
#include "tp_stub.h"

#define WINHOOK_IMPLEMENTATION
#define WINHOOK_NOINLINE
#define WINHOOK_STATIC 
#define MINHOOK_IMPLEMENTATION
#define MINHOOK_STATIC
#ifdef USECOMPAT
#include "winhook_v350.h"
#include "winversion_v100.h"
#include "stb_minhook_v1332.h"
#else
#include "winhook.h"
#include "winversion.h"
#include "stb_minhook.h"
#endif

// hxv4 functions and struct
typedef struct Hxv4CompoundHasher Hxv4CompoundHasher;
typedef  tjs_int(__fastcall *FuncHxv4CalcHash)(Hxv4CompoundHasher* _this, void* _edx,
    OUT tTJSVariant* hash, const tTJSString* str, const tTJSString* seed);

typedef struct Hxv4CompoundHasher
{
    struct
    {
        void* destruct;
        FuncHxv4CalcHash calc;
    } *vftable; // offset 0
    tjs_uint8* salt;  // offset 0x4
    tjs_int saltsize; // offset 0x8
} Hxv4CompoundHasher;

typedef struct Hxv4DirHasher
{
    Hxv4CompoundHasher base;
    tjs_uint8 saltdata[0x10];
} Hxv4DirHasher;

typedef struct Hxv4FileHasher
{
    Hxv4CompoundHasher base;
    tjs_uint8 saltdata[0x20];
} Hxv4FileHasher;

typedef struct Hxv4CompoundStorageMedia
{
    void* vftable;
    int nref;
    uint32_t reserve1;
    tTJSString prefix;
    tTJSString seed; //offset 0x10
    CRITICAL_SECTION critical_section;
    uint8_t reserve2[0x20];
    tTJSString* start;
    tTJSString* pos;
    tTJSString* end;
    Hxv4DirHasher* dirhasher; // offset 0x58
    Hxv4FileHasher* filehasher;
} Hxv4CompoundStorageMedia;

//  func type declear
DWORD WINAPI calc_thread(void *arg);
HRESULT __stdcall V2Link_hook(iTVPFunctionExporter* exporter);
tjs_error _cdecl CreateHxv4CompoundStorageMedia_hook(Hxv4CompoundStorageMedia **ret,
    tTJSVariant *prefix, int argc, char *argv[]);

// global value define
#define FILELISTNAME "files"
#define DIRLISTNAME "dirs"
HANDLE LoadlibraryW_mutex = nullptr;
decltype(LoadLibraryW) *LoadlibraryW_org = nullptr;
decltype(V2Link_hook) *V2Link_org = nullptr;
void *V2Link_old = nullptr;
decltype(CreateHxv4CompoundStorageMedia_hook) *CreateHxv4CompoundStorageMedia_org = nullptr;
void *CreateHxv4CompoundStorageMedia_old = nullptr;
const char *CreateHxv4CompoundStorageMedia_sig = (const char *)"55 8b ec 6a ff 68 ? ? ? ? 64 a1 00 00 00 00 50 83 ec 08 56 a1 ? ? ? ? 33 c5 50 8d 45 f4 64 a3 00 00 00 00 a1 ? ? ? ? 85 c0 75 12 68 ? ? ? ? e8 ? ? ? ? 83 c4 04 a3 ? ? ? ? 8b 75 0c 56 ff d0 83 f8 02 74 ? b8 15 fc ff ff 8b 4d f4 64 89 0d 00 00 00 00 59 5e 8b e5 5d c3";
const char *KrkrSignVerify_sig = (const char *)"55 8B EC 8B 4D 08 85 C9 74 13 FF 75 10 FF 75 0C";

HRESULT __stdcall V2Link_hook(iTVPFunctionExporter* exporter)
{
    LOGi("exporter %p\n", exporter);
    TVPInitImportStub(exporter); // must bind exporter here
    MH_DisableHook(V2Link_old);
    return V2Link_org(exporter);
}

// hook functions
tjs_error _cdecl CreateHxv4CompoundStorageMedia_hook(Hxv4CompoundStorageMedia **ret, tTJSVariant *prefix, int argc, char *argv[])
{
    auto err = CreateHxv4CompoundStorageMedia_org(ret, prefix, argc, argv);
    LOGi("Hxv4CompoundStorageMedia at %p\n", *ret);
    MH_DisableHook(CreateHxv4CompoundStorageMedia_old);
    CreateThread(NULL, 0, calc_thread, (LPVOID)*ret, 0, NULL);
    return err;
}

HMODULE LoadLibraryW_hook(LPCWSTR name)
{
    WaitForSingleObject(LoadlibraryW_mutex, INFINITE);
    auto hmod = LoadlibraryW_org(name);
    // LOGLi(L"LoadLibraryW name=%ls hmod=%p\n", name, hmod);
    if(wcsstr(name, L"krkr_"))
    {
        size_t dllsize = winhook_getimagesize(GetCurrentProcess(), hmod);
        LOGi("load cxdec.tpm dllbase=%p dllsize=0x%zx\n", hmod, dllsize);
        
        // hook V2Link
        auto addr = reinterpret_cast<void*>(GetProcAddress(hmod, "V2Link"));
        if(addr)
        {
            MH_CreateHook(addr,  reinterpret_cast<LPVOID>(V2Link_hook),  
                reinterpret_cast<LPVOID*>(&V2Link_org));
            LOGi("MH_CreateHook V2Link %p -> %p\n", addr, V2Link_hook);
            MH_EnableHook(addr);
            V2Link_old = addr;
        }

        // hook KrkrSign::VerifierImpl + 4
        addr = winhook_searchmemory((void*)hmod, dllsize, KrkrSignVerify_sig, NULL);
        LOGi("search KrkrSignVerify va=%p rva=%zx\n", addr, (size_t)addr - (size_t)hmod);
        if(addr)
        {
            uint8_t patchbuf[] = {0x31, 0xc0, 0x40, 0xc3}; // xor eax, eax; inc eax; retn;
            winhook_patchmemory(addr, patchbuf, sizeof(patchbuf));
        }

        // hook CreateHxv4CompoundStorageMedia
        addr = winhook_searchmemory((void*)hmod, dllsize, CreateHxv4CompoundStorageMedia_sig, NULL);
        LOGi("search CreateHxv4CompoundStorageMedia va=%p rva=%zx\n", addr, (size_t)addr - (size_t)hmod);
        if(addr)
        {
            MH_CreateHook(addr,  reinterpret_cast<LPVOID>(CreateHxv4CompoundStorageMedia_hook),  
                reinterpret_cast<LPVOID*>(&CreateHxv4CompoundStorageMedia_org));
            LOGi("MH_CreateHook CreateHxv4CompoundStorageMedia %p -> %p\n", addr, CreateHxv4CompoundStorageMedia_hook);
            MH_EnableHook(addr);
            CreateHxv4CompoundStorageMedia_old = addr;
        }
        MH_DisableHook(reinterpret_cast<LPVOID>(LoadLibraryW)); // must disable or has problem on dx2d
    }
    ReleaseMutex(LoadlibraryW_mutex);
    return hmod;
}

enum RegexNodeType
{
    RegexNodeLiteral,
    RegexNodeCharSet,
    RegexNodeSequence,
    RegexNodeAlternation,
    RegexNodeRepeat,
};

struct RegexNode
{
    RegexNodeType type = RegexNodeSequence;
    std::wstring literal;
    std::vector<wchar_t> chars;
    std::vector<RegexNode> children;
    int min_repeat = 0;
    int max_repeat = 0;
};

class FiniteRegexParser
{
public:
    explicit FiniteRegexParser(const wchar_t* pattern) : m_pattern(pattern), m_len(wcslen(pattern)) {}

    bool parse(RegexNode& out)
    {
        out = parse_expression();
        return m_ok && m_pos == m_len;
    }

private:
    const wchar_t* m_pattern;
    size_t m_len = 0;
    size_t m_pos = 0;
    bool m_ok = true;

    RegexNode parse_expression()
    {
        std::vector<RegexNode> alternatives;
        while (m_ok)
        {
            alternatives.push_back(parse_sequence());
            if (m_pos >= m_len || m_pattern[m_pos] != L'|') break;
            m_pos++;
        }

        if (alternatives.size() == 1) return alternatives[0];

        RegexNode node;
        node.type = RegexNodeAlternation;
        node.children = alternatives;
        return node;
    }

    RegexNode parse_sequence()
    {
        RegexNode node;
        node.type = RegexNodeSequence;
        while (m_ok && m_pos < m_len && m_pattern[m_pos] != L')' && m_pattern[m_pos] != L'|')
        {
            if (m_pattern[m_pos] == L'^' || m_pattern[m_pos] == L'$')
            {
                m_pos++;
                continue;
            }
            node.children.push_back(parse_quantifier(parse_atom()));
        }
        return node;
    }

    RegexNode parse_atom()
    {
        RegexNode node;
        if (m_pos >= m_len)
        {
            m_ok = false;
            return node;
        }

        wchar_t ch = m_pattern[m_pos++];
        if (ch == L'(')
        {
            node = parse_expression();
            if (m_pos >= m_len || m_pattern[m_pos] != L')')
            {
                m_ok = false;
                return node;
            }
            m_pos++;
            return node;
        }

        if (ch == L'[')
        {
            node.type = RegexNodeCharSet;
            if (m_pos < m_len && m_pattern[m_pos] == L'^')
            {
                m_ok = false;
                return node;
            }

            while (m_ok && m_pos < m_len && m_pattern[m_pos] != L']')
            {
                wchar_t first = read_class_char();
                if (m_pos + 1 < m_len && m_pattern[m_pos] == L'-' && m_pattern[m_pos + 1] != L']')
                {
                    m_pos++;
                    wchar_t last = read_class_char();
                    if (first > last)
                    {
                        m_ok = false;
                        break;
                    }
                    for (wchar_t c = first; c <= last; c++)
                        node.chars.push_back(c);
                }
                else
                {
                    node.chars.push_back(first);
                }
            }

            if (m_pos >= m_len || m_pattern[m_pos] != L']')
            {
                m_ok = false;
                return node;
            }
            m_pos++;
            return node;
        }

        if (ch == L'\\')
        {
            if (m_pos >= m_len)
            {
                m_ok = false;
                return node;
            }

            wchar_t escaped = m_pattern[m_pos++];
            if (escaped == L'd')
            {
                node.type = RegexNodeCharSet;
                for (wchar_t c = L'0'; c <= L'9'; c++) node.chars.push_back(c);
                return node;
            }
            if (escaped == L'w')
            {
                node.type = RegexNodeCharSet;
                for (wchar_t c = L'0'; c <= L'9'; c++) node.chars.push_back(c);
                for (wchar_t c = L'A'; c <= L'Z'; c++) node.chars.push_back(c);
                for (wchar_t c = L'a'; c <= L'z'; c++) node.chars.push_back(c);
                node.chars.push_back(L'_');
                return node;
            }

            node.type = RegexNodeLiteral;
            node.literal.assign(1, escaped);
            return node;
        }

        if (ch == L'.' || ch == L'*' || ch == L'+')
        {
            m_ok = false;
            return node;
        }

        node.type = RegexNodeLiteral;
        node.literal.assign(1, ch);
        return node;
    }

    RegexNode parse_quantifier(RegexNode atom)
    {
        if (!m_ok || m_pos >= m_len) return atom;

        if (m_pattern[m_pos] == L'?')
        {
            m_pos++;
            RegexNode node;
            node.type = RegexNodeRepeat;
            node.children.push_back(atom);
            node.min_repeat = 0;
            node.max_repeat = 1;
            return node;
        }

        if (m_pattern[m_pos] != L'{') return atom;

        m_pos++;
        int min_repeat = parse_number();
        int max_repeat = min_repeat;
        if (m_pos < m_len && m_pattern[m_pos] == L',')
        {
            m_pos++;
            max_repeat = parse_number();
        }
        if (m_pos >= m_len || m_pattern[m_pos] != L'}' || min_repeat < 0 || max_repeat < min_repeat)
        {
            m_ok = false;
            return atom;
        }
        m_pos++;

        RegexNode node;
        node.type = RegexNodeRepeat;
        node.children.push_back(atom);
        node.min_repeat = min_repeat;
        node.max_repeat = max_repeat;
        return node;
    }

    int parse_number()
    {
        if (m_pos >= m_len || m_pattern[m_pos] < L'0' || m_pattern[m_pos] > L'9')
        {
            m_ok = false;
            return -1;
        }

        int value = 0;
        while (m_pos < m_len && m_pattern[m_pos] >= L'0' && m_pattern[m_pos] <= L'9')
        {
            value = value * 10 + (m_pattern[m_pos] - L'0');
            m_pos++;
        }
        return value;
    }

    wchar_t read_class_char()
    {
        if (m_pos >= m_len)
        {
            m_ok = false;
            return 0;
        }

        if (m_pattern[m_pos] == L'\\')
        {
            m_pos++;
            if (m_pos >= m_len)
            {
                m_ok = false;
                return 0;
            }
        }
        return m_pattern[m_pos++];
    }
};

static bool expand_regex_node(const RegexNode& node, std::wstring& current,
    const std::function<bool(const std::wstring&)>& callback);

static bool expand_regex_sequence(const std::vector<RegexNode>& children, size_t index,
    std::wstring& current, const std::function<bool(const std::wstring&)>& callback)
{
    if (index >= children.size()) return callback(current);
    return expand_regex_node(children[index], current, [&](const std::wstring&) {
        return expand_regex_sequence(children, index + 1, current, callback);
    });
}

static bool expand_regex_repeat(const RegexNode& child, int remaining, std::wstring& current,
    const std::function<bool(const std::wstring&)>& callback)
{
    if (remaining == 0) return callback(current);
    return expand_regex_node(child, current, [&](const std::wstring&) {
        return expand_regex_repeat(child, remaining - 1, current, callback);
    });
}

static bool expand_regex_node(const RegexNode& node, std::wstring& current,
    const std::function<bool(const std::wstring&)>& callback)
{
    size_t old_size = current.size();
    switch (node.type)
    {
    case RegexNodeLiteral:
        current += node.literal;
        if (!callback(current)) return false;
        current.resize(old_size);
        return true;
    case RegexNodeCharSet:
        for (wchar_t ch : node.chars)
        {
            current.push_back(ch);
            if (!callback(current)) return false;
            current.resize(old_size);
        }
        return true;
    case RegexNodeSequence:
        return expand_regex_sequence(node.children, 0, current, callback);
    case RegexNodeAlternation:
        for (const auto& child : node.children)
        {
            current.resize(old_size);
            if (!expand_regex_node(child, current, callback)) return false;
        }
        current.resize(old_size);
        return true;
    case RegexNodeRepeat:
        for (int i = node.min_repeat; i <= node.max_repeat; i++)
        {
            current.resize(old_size);
            if (!expand_regex_repeat(node.children[0], i, current, callback)) return false;
        }
        current.resize(old_size);
        return true;
    }
    return false;
}

// Per-regex safety limit. This is not the old enumeration size; it prevents user-authored regex rules from exploding.
static const size_t REGEX_EXPANSION_LIMIT = 10000;

static size_t add_regex_count(size_t a, size_t b)
{
    if(a > REGEX_EXPANSION_LIMIT || b > REGEX_EXPANSION_LIMIT ||
        a > REGEX_EXPANSION_LIMIT + 1 - b)
        return REGEX_EXPANSION_LIMIT + 1;
    return a + b;
}

static size_t multiply_regex_count(size_t a, size_t b)
{
    if(a == 0 || b == 0) return 0;
    if(a > REGEX_EXPANSION_LIMIT || b > REGEX_EXPANSION_LIMIT ||
        a > (REGEX_EXPANSION_LIMIT + 1) / b)
        return REGEX_EXPANSION_LIMIT + 1;
    return a * b;
}

static size_t pow_regex_count(size_t value, int exponent)
{
    size_t result = 1;
    for(int i = 0; i < exponent; i++)
        result = multiply_regex_count(result, value);
    return result;
}

static size_t count_regex_node(const RegexNode& node)
{
    switch(node.type)
    {
    case RegexNodeLiteral:
        return 1;
    case RegexNodeCharSet:
        return node.chars.size();
    case RegexNodeSequence:
    {
        size_t total = 1;
        for(const auto& child : node.children)
            total = multiply_regex_count(total, count_regex_node(child));
        return total;
    }
    case RegexNodeAlternation:
    {
        size_t total = 0;
        for(const auto& child : node.children)
            total = add_regex_count(total, count_regex_node(child));
        return total;
    }
    case RegexNodeRepeat:
    {
        size_t child_count = count_regex_node(node.children[0]);
        size_t total = 0;
        for(int i = node.min_repeat; i <= node.max_repeat; i++)
            total = add_regex_count(total, pow_regex_count(child_count, i));
        return total;
    }
    }
    return REGEX_EXPANSION_LIMIT + 1;
}

static bool expand_regex_pattern(const wchar_t* pattern, size_t* out_count,
    const std::function<bool(const std::wstring&)>& callback)
{
    RegexNode root;
    FiniteRegexParser parser(pattern);
    if (!parser.parse(root)) return false;

    size_t expected_count = count_regex_node(root);
    if(out_count) *out_count = expected_count;
    if(expected_count > REGEX_EXPANSION_LIMIT) return false;

    size_t expanded = 0;
    std::wstring current;
    return expand_regex_node(root, current, [&](const std::wstring& value) {
        if (++expanded > REGEX_EXPANSION_LIMIT) return false;
        return callback(value);
    });
}

const wchar_t* WINAPI calc_name_hexify(Hxv4CompoundHasher *hasher, tTJSString *seed, const wchar_t* name)
{
    static wchar_t hashstrw[0x64] = {0};
    tTJSVariant hashvar;
    tTJSString targetstr(name);
    tjs_int hashsize = hasher->vftable->calc(hasher, nullptr, &hashvar, &targetstr, seed);
    tTJSVariantOctet* hashoctet = hashvar.AsOctetNoAddRef();
    const uint8_t* data = hashoctet->GetData();
    inl_hexifyw(hashstrw, sizeof(hashstrw)/2, data, hashsize, nullptr);
    return hashstrw;
}

static void write_hash_line(FILE* fp, Hxv4CompoundHasher *hasher, tTJSString *seed, const wchar_t* name)
{
    const wchar_t *hashstrw = calc_name_hexify(hasher, seed, name);
    LOGLi(L"%ls,%ls\n", name, hashstrw);
    fwrite(name, 2, wcslen(name), fp);
    fwrite(L",", 2, 1, fp);
    fwrite(hashstrw, 2, wcslen(hashstrw), fp);
    fwrite(L"\r\n", 2, 2, fp);
}

DWORD WINAPI calc_list(Hxv4CompoundHasher *hasher, tTJSString *seed, const char *inpath, const char *outpath)
{
    int i = 0;
    uint16_t bom;
    static wchar_t linestrw[0x200];
    FILE *fp1 = fopen(inpath, "rb");
    FILE *fp2 = fopen(outpath, "wb");
    if(!fp1 || !fp2)
    {
        if(fp1) fclose(fp1);
        if(fp2) fclose(fp2);
        LOGe("open list failed %s -> %s\n", inpath, outpath);
        return 0;
    }
    fwrite("\xff\xfe", 1, 2, fp2);
    fread(&bom, 2, 1, fp1);
    if(bom != 0xfeff) fseek(fp1, 0, SEEK_SET);
    while(fgetws(linestrw, sizeof(linestrw)/2, fp1))
    {
        size_t len = wcslen(linestrw);
        while(len > 0 && (linestrw[len - 1] == L'\r' || linestrw[len - 1] == L'\n'))
            linestrw[--len] = 0;
        if(len == 0) continue;

        if(_wcsnicmp(linestrw, L"regex:", 6) == 0)
        {
            int regex_count = 0;
            size_t expected_count = 0;
            LOGLi(L"expand regex: %ls\n", linestrw + 6);
            bool ok = expand_regex_pattern(linestrw + 6, &expected_count, [&](const std::wstring& name) {
                write_hash_line(fp2, hasher, seed, name.c_str());
                i++;
                regex_count++;
                return true;
            });
            if(!ok)
            {
                LOGLi(L"regex expand failed or exceeded limit (%zu): %ls\n", expected_count, linestrw + 6);
            }
            else
            {
                LOGi("regex expanded %d names\n", regex_count);
            }
        }
        else
        {
            write_hash_line(fp2, hasher, seed, linestrw);
            i++;
        }
        fflush(fp2);
    }
    fclose(fp1);
    fclose(fp2);
    return i;
}

DWORD WINAPI calc_thread(void *arg)
{
    // Sleep(400); // simply wait for V2Link finish
    auto media = reinterpret_cast<Hxv4CompoundStorageMedia*>(arg);
    auto filehasher = media->filehasher;
    auto dirhasher = media->dirhasher;

    FILE *fp1;
    FILE *fp2;
    uint16_t bom;
    wchar_t tmp[0x100];
    wchar_t linestrw[0x200];
    wchar_t hashstrw[0x64] = {0};
    LOGLi(L"seed=%ls\n", media->seed.c_str());

    LOGi("try to calc names in %s\n", FILELISTNAME".txt");
    calc_list(&filehasher->base, &media->seed, FILELISTNAME".txt", FILELISTNAME"_match.txt");
    LOGi("try to calc names in %s\n", DIRLISTNAME".txt");
    calc_list(&dirhasher->base, &media->seed, DIRLISTNAME".txt", DIRLISTNAME"_match.txt");
    LOGi("calculate finish, results in %s, %s\n", FILELISTNAME"_match.txt", DIRLISTNAME"_match.txt");

    return 0;
}

static void init()
{
    AllocConsole();
    freopen("CONOUT$", "w", stdout);
    // system("chcp 936");
    // setlocale(LC_ALL, "chs");
    printf("krkr_hxv4_hash calculator, v0.1.1, developed by devseed\n");
    
    DWORD winver = GetVersion();
    DWORD winver_major = (DWORD)(LOBYTE(LOWORD(winver)));
    DWORD winver_minor = (DWORD)(HIBYTE(LOWORD(winver)));
    LOGi("version NT=%lu.%lu\n", winver_major, winver_minor);
    #if defined(_MSC_VER)
    LOGi("compiler MSVC=%d\n", _MSC_VER)
    #elif defined(__GNUC__)
    LOGi("compiler GNUC=%d.%d\n", __GNUC__, __GNUC_MINOR__);
    #elif defined(__TINYC__)
    LOGi("compiler TCC\n");
    #endif

    auto status = MH_Initialize();
    if(status != MH_OK)
    {
        LOGe("MH_Initialize failed\n");
        return;
    }

    LoadlibraryW_mutex = CreateMutexA(NULL, FALSE, NULL);
    status = MH_CreateHook(reinterpret_cast<LPVOID*>(LoadLibraryW), 
        reinterpret_cast<LPVOID*>(LoadLibraryW_hook), 
        reinterpret_cast<LPVOID*>(&LoadlibraryW_org));
    LOGi("MH_CreateHook LoadLibraryW %p -> %p\n", LoadLibraryW, LoadLibraryW_hook);
    status = MH_EnableHook(reinterpret_cast<LPVOID>(LoadLibraryW));
    if(status != MH_OK)
    {
        LOGe("MH_EnableHook LoadLibraryW failed");
        return;
    }
}

BOOL WINAPI DllMain(HINSTANCE hinstDLL,  DWORD fdwReason,  LPVOID lpReserved )
{
    switch( fdwReason ) 
    { 
        case DLL_PROCESS_ATTACH:
            winversion_init();
            init();
            break;
        case DLL_THREAD_ATTACH:
            break;
        case DLL_THREAD_DETACH:
            break;
        case DLL_PROCESS_DETACH:
            break;
    }
    return TRUE;
}

/**
 * history
 * v0.1, initial version
 * v0.1.1, add KrkrSign patch
 */
