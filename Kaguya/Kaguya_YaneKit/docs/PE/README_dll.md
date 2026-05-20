# DLL 分析记录

本文只记录 `Graphics.dll`、`RenderDX.dll`、`Sound.dll` 三个运行时模块，不把它们和 `.scr` / `params.dat` / `message.dat` 混在一起。

## 1. 总体关系

已确认的加载链如下：

```text
Start.exe
  -> Lib\\Graphics.dll
      -> RenderDX.dll / RenderGL.dll / GDI backend
  -> Lib\\sound.dll
```

其中：
- `Start.exe` 是主程序，负责把运行时对象交给 `Graphics.dll`
- `Graphics.dll` 是图形桥接层，负责根据模式选择渲染后端并对外导出高层图形接口
- `RenderDX.dll` 是当前主路径下的 Direct3D 渲染后端
- `Sound.dll` 是音频后端

## 2. Start.exe -> Graphics.dll

`sub_43A520` 是当前确认到的图形初始化入口。它做了几件事：

1. 组装 `Lib\\Graphics.dll`
2. `LoadLibraryA`
3. `GetProcAddress("InitSystem")`
4. 把主程序侧的运行参数传给 `Graphics.dll`

关键结论：
- `Graphics.dll` 不是被动资源库，而是整个图形子系统的启动入口
- `InitSystem` 成功后，后续图形对象都由它持有和转发

`sub_5138A0` 是当前确认到的音频初始化入口。它装载 `Lib\\sound.dll`，解析 `InitSystem`，随后主程序侧再通过 `AddSound`、`SoundOpen`、`SoundPlay` 等导出完成音频资源和播放控制。

## 3. Graphics.dll

`Graphics.dll` 的定位是“包装层 + 资源对象层”。

### 3.1 后端装载

`sub_10001000` 和 `sub_10002B10` 都是这类装载/初始化包装器：

- 先创建内部 `CLoadLibrary`
- 再 `LoadLibraryA` 载入后端 DLL
- 然后 `GetProcAddress("Initialize")`
- 最后把初始化参数传给后端

`Graphics.dll::InitSystem` 会按模式创建不同后端对象：

- `a1 == 1`：`Graphics::CRenderDX`
- `a1 == 2`：`Graphics::CRenderGL`
- 其他：`Graphics::CRenderGDI`

当前项目实际确认到的主路径是 `RenderDX.dll`。代码里还能看到同族的 `RenderGL.dll` / GDI 分支；GDI 分支已经能从 `Graphics.dll` 内部反推出对象与 HDC 输出链路，`RenderGL.dll` 则只有 wrapper 侧代码和字符串，当前仓库/样本树未包含 `RenderGL.dll` 本体。

### 3.2 关键转发

`Graphics.dll` 里有一批明显的转发层导出：

- `RenderSceen`
- `RenderSceenHDC`
- `SurfaceLoadBMP`
- `SurfaceLoadALP`
- `SurfaceLoadANM`
- `SurfaceClear`
- `SurfaceBlt`
- `SurfaceBlendBlt`
- `TextDraw`
- `TextBlt`
- `DIBitmapCreate`
- `DIBitmapDraw`
- `DIBitmapRelease`

其中：
- `RenderSceen` / `RenderSceenHDC` 最终转到 `RenderDX.dll`
- `SurfaceLoadALP` / `SurfaceLoadANM` 会先触发对象侧的状态更新，再进入具体资源加载函数

### 3.3 内部对象

已确认出现的对象/类包括：
- `CLoadLibrary`
- `Graphics::CRenderDX`
- `Graphics::CRenderGL`
- `Graphics::CRenderGDI`
- `Graphics::CDIBitmap`
- `Graphics::CDirtyRect`
- `Graphics::CTextBlt`

字段级命名已经可以按 hook 粒度固定如下：

| 对象 | 字段/偏移 | 建议名 | 含义 |
| --- | --- | --- | --- |
| 全局 | `dword_1007A2B8` | `g_renderArrayBase` | 当前渲染对象数组/单例存储起点 |
| 全局 | `dword_1007A2BC` | `g_renderActiveIndex` | 当前活动渲染对象索引 |
| 全局 | `dword_1007A2C0` | `g_renderArrayRef` | 数组引用对象，`+0x0C` 为元素 stride |
| `CRenderDX/CRenderGL` | `+0x00` | `vftable` | C++ 虚表 |
| `CRenderDX/CRenderGL` | `+0x10` | `screenWidth` | 初始化后写入的屏幕宽度 |
| `CRenderDX/CRenderGL` | `+0x14` | `screenHeight` | 初始化后写入的屏幕高度 |
| `CRenderDX/CRenderGL` | `+0x30` | `backendLibraryArrayBase` | 后端 DLL `CLoadLibrary` 存储 |
| `CRenderDX/CRenderGL` | `+0x34` | `backendLibraryIndex` | 当前后端 DLL 索引 |
| `CRenderDX/CRenderGL` | `+0x38` | `backendLibraryArrayRef` | 后端 DLL 数组引用对象 |
| `CLoadLibrary` | `+0x00` | `vftable` | 生命周期管理虚表 |
| `CLoadLibrary` | `+0x04` | `moduleHandle` | `LoadLibraryA` 返回的 `HMODULE` |

资源对象的可 hook 命名：

| 函数/对象 | 建议名 | 作用 |
| --- | --- | --- |
| `sub_1000D970` | `ValidateSurfaceHandle` | 校验/解析 surface handle |
| `sub_1000DB60` | `CreateSurfaceObject` | 创建 surface 对象 |
| `sub_1000DFB0` | `LoadBmpToSurface` | BMP 像素装载 |
| `sub_1000E110` | `LoadAlpToSurface` | AP/AP-0/AP-2/AP-3 像素装载 |
| `sub_1000E2B0` | `LoadAnmPixelsToSurface` | ANM 已解码像素装载 |
| `sub_1003B010` | `CreateTextFontObject` | 字体对象创建 |
| `sub_1003C150` | `DrawTextToSurface` | 文本绘制 |

`Graphics::CRenderGDI` 字段布局和 DX/GL 不完全相同，但已经能确认两条路径：

- `sub_10006010` 是外部 GDI 后端装载路径，尝试 `LoadLibraryA("Lib\\RenderGDIp.dll")`，随后解析 `Initialize` 并写入 `screenWidth/screenHeight/mode`。
- `sub_100071E0 -> sub_10006F80` 是内部 DIB surface 路径，直接通过 GDI `CreateDIBSection` 创建 top-down DIB，再用 `BitBlt/StretchBlt` 输出到调用方 HDC。

## 4. RenderDX.dll

`RenderDX.dll` 是真正的渲染后端。

### 4.1 已确认导出

```text
Initialize
RenderSceen
RenderSceenHDC
RenderReset
UpdatePrimary
Resize
GetBits
GetPitch
GetMagFiltering
GetMinFiltering
SetMagFiltering
SetMinFiltering
IsFiltering
TestCooperativeLevel
Release
GetErrorMessage
```

### 4.2 语义结论

- 它负责设备初始化、主表面更新、分辨率切换和过滤设置
- `GetBits` / `GetPitch` 说明它直接管理可访问的像素缓冲
- `RenderSceen` / `RenderSceenHDC` 是最终提交到屏幕的出口

`RenderDX.dll` 里还能看到 `d3d9.dll` / `d3d9d.dll` 的动态装载痕迹，说明它确实是 Direct3D 后端，而不是脚本 VM 或别的虚拟层。

## 5. Sound.dll

`Sound.dll` 是独立音频后端。

### 5.1 已确认导出

```text
InitSystem
AddSound
SoundOpen
SoundPlay
SoundStop
SoundPause
SoundClose
SoundSetLoop
SoundSetVolume
SoundGetVolume
SoundIsPlay
SoundIsLoop
ResetDevice
ActivateAppRestore
SetPrimaryVolume
SetPrimaryBufferFormat
GetDefaultDeviceID
```

### 5.2 语义结论

- `InitSystem` 初始化音频主系统
- `AddSound` 是资源/对象注册入口
- `SoundOpen` 会在内部音频对象表里寻找匹配对象，再进入实际打开流程
- `SoundPlay` / `SoundStop` / `SoundPause` 是播放控制

`SoundOpen` 里已经能看到一个全局单例式的 `CDirectSound` 风格对象，以及内部列表查找逻辑。

## 6. 当前结论

1. `Graphics.dll` 不是最终渲染器，它是包装层。
2. `RenderDX.dll` 才是实际图形后端。
3. `Sound.dll` 是独立音频后端。
4. `Start.exe` 负责把这些模块串起来。

## 7. 对象字段和链路命名

本节字段名不是源码符号，而是根据 IDA 访问方式和导出行为确认的业务名。

### 7.1 `Graphics.dll` 对象字段

全局活动渲染器槽：

| 地址/字段 | 建议名 | 含义 |
| --- | --- | --- |
| `dword_1007A2B8` | `g_renderArrayBase` | 当前渲染对象数组/单例存储起点 |
| `dword_1007A2BC` | `g_renderActiveIndex` | 当前活动渲染对象索引 |
| `dword_1007A2C0` | `g_renderArrayRef` | 数组引用对象；`+0x0C` 为元素 stride |

`Graphics.dll` 的绝大多数导出都先按下面公式取得当前渲染对象，再走 vtable 或内部 helper：

```text
render = g_renderArrayBase + g_renderActiveIndex * g_renderArrayRef->stride
```

`Graphics::CRenderDX / CRenderGL` 已确认字段：

| 偏移 | 建议名 | 依据 |
| ---: | --- | --- |
| `+0x00` | `vftable` | C++ 虚表 |
| `+0x10` | `screenWidth` | `sub_10001000 / sub_10002B10` 初始化后写入 |
| `+0x14` | `screenHeight` | 同上 |
| `+0x30` | `backendLibraryArrayBase` | 后端 DLL `CLoadLibrary` 存储 |
| `+0x34` | `backendLibraryIndex` | 后端 DLL 当前索引 |
| `+0x38` | `backendLibraryArrayRef` | 后端 DLL 数组引用对象 |

`Graphics::CRenderGDI` / GDI-DIB 路径已确认字段：

| 对象/偏移 | 建议名 | 依据 |
| --- | --- | --- |
| `CRenderGDI +0x00` | `vftable` | `RenderSceen/RenderSceenHDC` 通过当前渲染对象 vtable 分派 |
| `CRenderGDI +0x04` | `dibOrBackendArrayBase` | `sub_100071E0` 创建 `Graphics::CDIBitmap` 后挂入该槽；`sub_10006010` 创建 `CLoadLibrary` 后也复用同类数组包装 |
| `CRenderGDI +0x08` | `dibOrBackendIndex` | 当前 DIB/backend 元素索引 |
| `CRenderGDI +0x0C` | `dibOrBackendArrayRef` | 数组引用对象；`+0x0C` 为元素 stride |
| `CRenderGDI +0x10` | `modeOrWindowHandle` | 初始化后写入的第一个上层参数；外部 GDI 后端路径传给 `Initialize` |
| `CRenderGDI +0x14` | `screenWidth` | `sub_100071E0/sub_10006010` 初始化成功后写入 |
| `CRenderGDI +0x18` | `screenHeight` | 同上 |
| `CLoadLibrary +0x04` | `moduleHandle` | `Lib\\RenderGDIp.dll` 的 `HMODULE`，随后 `GetProcAddress("Initialize")` |
| `CDIBitmap +0x04` | `dibBitCount` | `sub_10006F80` 写入 DIB bpp |
| `CDIBitmap +0x08` | `hBitmap` | `CreateDIBSection` 返回的 `HBITMAP` |
| `CDIBitmap +0x0C` | `memoryHdc` | `CreateCompatibleDC(NULL)`，并 `SelectObject(hBitmap)` |
| `CDIBitmap +0x10` | `pixelDescArrayBase` | 指向像素描述对象数组 |
| `CDIBitmap +0x14` | `pixelDescIndex` | 当前像素描述索引 |
| `CDIBitmap +0x18` | `pixelDescArrayRef` | 数组引用对象 |
| `PixelDesc +0x04` | `valid` | DIB 创建成功后置 `1`，析构/失败时清 `0` |
| `PixelDesc +0x08` | `bits` | `CreateDIBSection` 返回的像素指针 |
| `PixelDesc +0x0C` | `pitch` | `(width * bytesPerPixel + 3) & ~3` |
| `PixelDesc +0x10` | `width` | DIB 宽度 |
| `PixelDesc +0x14` | `height` | DIB 高度 |
| `PixelDesc +0x1C` | `pixelFormatCode` | 15/16bpp=`4`，24bpp=`5`，32bpp=`7`，带 alpha 的 32bpp=`12` |

GDI 输出链路：

```text
RenderSceenHDC
  -> 当前 render vtable +0x08
     -> CRenderGDI::RenderScreenHDC wrapper
        -> 若使用外部 backend：GetProcAddress("RenderSceenHDC")
        -> 若使用内部 DIB：CDIBitmap::BitBlt / CDIBitmap::StretchBlt
           -> BitBlt / SetStretchBltMode(HALFTONE) / StretchBlt
```

这条链的低层 hook 点已经闭合：`CreateDIBSection`、`CreateCompatibleDC`、`SelectObject`、`BitBlt`、`StretchBlt`、`DeleteDC`、`DeleteObject`，以及 `Graphics.dll!RenderSceenHDC` 导出和当前 render vtable。

`RenderGL.dll` wrapper 已确认字段与 DX 同族，`sub_10002B10` 固定装载 `"RenderGL.dll"`，解析 `Initialize`，随后解析 `GetPitch/GetBits` 并把返回值写入内部 `PixelDesc`：

| wrapper 行为 | 结论 |
| --- | --- |
| `LoadLibraryA(lpLibFileName)`，调试字符串为 `RenderGL.dll` | GL 后端是外部 DLL，不在 `Graphics.dll` 内实现 |
| `GetProcAddress("Initialize")` | 初始化签名为 `Initialize(a2, width, height)` 形态 |
| `GetProcAddress("GetPitch")` | GL 后端必须暴露 pitch |
| `GetProcAddress("GetBits")` | GL 后端必须暴露 primary bits |
| `PixelDesc.valid=1, pixelFormatCode=7` | wrapper 将 GL 后端按 32bpp primary surface 暴露给 `Graphics.dll` 上层 |

当前 `func` 与 `v5.8` 样本树内没有 `RenderGL.dll` 本体，因此不能给出其内部设备对象字段；能确认并可 hook 的边界是 `Graphics.dll` 的 GL wrapper、`LoadLibraryA/GetProcAddress`、以及可替换的同名 `RenderGL.dll` proxy。

资源对象层：

| 对象/函数 | 建议名 | 说明 |
| --- | --- | --- |
| `sub_1000D970` | `ValidateSurfaceHandle` | 多数 `Surface*` 导出先用它校验/解析 surface 句柄 |
| `sub_1000DB60` | `CreateSurfaceObject` | `SurfaceCreate` 的实际对象创建 |
| `sub_1000DFB0` | `LoadBmpToSurface` | `SurfaceLoadBMP` 实际像素装载 |
| `sub_1000E110` | `LoadAlpToSurface` | `SurfaceLoadALP` 实际像素装载 |
| `sub_1000E2B0` | `LoadAnmPixelsToSurface` | `SurfaceLoadANM` 最终拷贝已解码像素；不是 ANM parser |
| `sub_1003B010` | `CreateTextFontObject` | `TextCreateFont` 的实际字体对象创建 |
| `sub_1003C150` | `DrawTextToSurface` | `TextDraw` 的实际绘制 |

文本链路仍是 ANSI/GDI：

```text
TextCreateFont -> CreateFontIndirectA
TextDraw/TextBlt -> TextOutA / GetTextExtentPoint32A
```

因此文本 hook 的最终落点可以是 `Graphics.dll` 导出，也可以是 GDI IAT。


### 7.2 `RenderDX.dll` 设备状态/表面状态流

全局活动 D3D 后端槽：

| 地址/字段 | 建议名 | 含义 |
| --- | --- | --- |
| `off_1006A764` | `g_renderDxRoot` | `Initialize` 传入的根对象 |
| `dword_1006A768` | `g_direct3DArrayBase` | `Graphics::CDirect3D` 对象存储 |
| `dword_1006A76C` | `g_direct3DActiveIndex` | 当前活动 `CDirect3D` 索引 |
| `dword_1006A770` | `g_direct3DArrayRef` | 数组引用对象；`+0x0C` 为 stride |

`Graphics::CDirect3D` 已确认字段：

| 偏移 | 建议名 | 依据 |
| ---: | --- | --- |
| `+0x00` | `vftable` | `Graphics::CDirect3D::vftable` |
| `+0x04` | `d3d9` | `Direct3DCreate9(0x20)` 返回的 `IDirect3D9*` |
| `+0x08` | `deviceArrayBase` | `CD3DDevice` 存储起点 |
| `+0x0C` | `deviceActiveIndex` | 当前设备索引 |
| `+0x10` | `deviceArrayRef` | 数组引用对象 |
| `+0x14` | `displayModeArrayBegin` | adapter/display mode 候选表 |
| `+0x18` | `displayModeArrayEnd` | 同上 |

`Graphics::CD3DDevice` 对象由 `sub_10003CC0` 创建，大小 `0x7C`：

| 偏移 | 建议名 | 依据 |
| ---: | --- | --- |
| `+0x00` | `vftable` | `Graphics::CD3DDevice::vftable` |
| `+0x04` | `d3dDevice9` | `IDirect3DDevice9*` |
| `+0x08` | `presentParameters` | `D3DPRESENT_PARAMETERS` 相关拷贝，长度约 `0x38` |
| `+0x40` | `primarySurfaceValid` | 主 surface/lockable surface 创建结果 |
| `+0x44` | `primarySurfaceBitsOrFormat` | `CreateOffscreenPlainSurface/GetDesc` 后写入 |
| `+0x50` | `primarySurfaceArrayBase` | `UpdatePrimary` 读取的主表面槽 |
| `+0x54` | `primarySurfaceIndex` | 当前主表面索引 |
| `+0x58` | `primarySurfaceArrayRef` | 表面数组引用对象 |
| `+0x5C` | `backSurfaceArrayBase` | 备用/后备表面槽 |
| `+0x60` | `backSurfaceIndex` | 当前后备表面索引 |
| `+0x64` | `backSurfaceArrayRef` | 后备表面数组引用对象 |
| `+0x68` | `viewportX` | `RenderSceen` 绘制矩形左上 X |
| `+0x6C` | `viewportY` | `RenderSceen` 绘制矩形左上 Y |
| `+0x70` | `viewportWidth` | `RenderSceen` 使用 |
| `+0x74` | `viewportHeight` | `RenderSceen` 使用 |

主状态流：

```text
Initialize
  -> sub_10001590
  -> sub_10003CC0
     -> Direct3DCreate9
     -> IDirect3D9::GetAdapterDisplayMode
     -> IDirect3D9::CheckDeviceType / CheckDeviceFormat / CheckDepthStencilMatch
     -> IDirect3D9::CreateDevice
     -> Create primary/offscreen surfaces

UpdatePrimary
  -> sub_10001930
     -> copy/update primary surface through D3D surface vtable

RenderSceen
  -> sub_10002020
     -> IDirect3DDevice9::Clear
     -> BeginScene
     -> draw primary surface quad
     -> EndScene
     -> Present
     -> on D3DERR_DEVICELOST: reset path
```

### 7.3 `Sound.dll` 对象表和资源句柄字段

全局对象：

| 地址/字段 | 建议名 | 含义 |
| --- | --- | --- |
| `dword_10084630` | `g_directSoundSystem` | `KaGuYa::Sound::CDirectSound` 单例 |
| `unk_100847A4` | `g_directSoundSystemMutex` | 单例初始化锁 |
| `dword_1008462C` | `g_soundParameter` | 全局音量/参数对象 |
| `unk_10084774` | `g_soundParameterMutex` | 参数对象初始化锁 |

`KaGuYa::Sound::CDirectSound` 已确认字段：

| 偏移 | 建议名 | 依据 |
| ---: | --- | --- |
| `+0x00` | `vftable` | `KaGuYa::Sound::CDirectSound::vftable` |
| `+0x04` | `directSound8` | `sub_1000FC60` 调用 `DirectSoundCreate8(NULL, this+1, NULL)` 写入；随后 vtable `+0x18` 调 `SetCooperativeLevel(hwnd, DSSCL_PRIORITY)` |
| `+0x08` | `primaryBuffer` | `sub_1000F110` 通过 `IDirectSound8::CreateSoundBuffer` 创建；`GetPrimaryVolume/SetPrimaryVolume` 调该对象 vtable `+0x18/+0x3C` |
| `+0x0C` | `soundListHead` | `SoundOpen/Play/Stop/...` 遍历的 list head |
| `+0x10` | `soundCount` | `AddSound` 插入后递增 |

DirectSound COM 字段与 vtable 调用：

| 字段/调用 | COM 接口语义 | 依据 |
| --- | --- | --- |
| `directSound8 + vtable[3]` / `+0x0C` | `IDirectSound8::CreateSoundBuffer` | `sub_1000F110` 用 `DSBUFFERDESC.dwSize=36`、`dwFlags=0x81` 创建 primary buffer |
| `directSound8 + vtable[6]` / `+0x18` | `IDirectSound8::SetCooperativeLevel` | `sub_1000FC60` 在 `DirectSoundCreate8` 后传入窗口句柄和 `2` |
| `directSound8 + vtable[2]` / `+0x08` | `IUnknown::Release` | `ResetDevice/sub_1000ECA0` 释放 `+0x04` |
| `primaryBuffer + vtable[6]` / `+0x18` | `IDirectSoundBuffer::GetVolume` | `GetPrimaryVolume` 读取 dB 音量 |
| `primaryBuffer + vtable[14]` / `+0x38` | `IDirectSoundBuffer::SetFormat` | `sub_1000F110` 写入 `WAVEFORMATEX` |
| `primaryBuffer + vtable[15]` / `+0x3C` | `IDirectSoundBuffer::SetVolume` | `SetPrimaryVolume` 写入 dB 音量 |
| `primaryBuffer + vtable[2]` / `+0x08` | `IUnknown::Release` | `ResetDevice/sub_1000ECA0` 释放 `+0x08` |

`SetPrimaryBufferFormat` 的参数映射已经确认：

| 参数文本 | 实际值 |
| --- | --- |
| `stereo` / `mono` | `nChannels = 2 / 1` |
| `11.025kHz` / `22.05kHz` / `44.1kHz` | `nSamplesPerSec = 11025 / 22050 / 44100` |
| `8bit` / `16bit` | `wBitsPerSample = 8 / 16` |

`sub_1000F110` 生成 `WAVEFORMATEX`：`wFormatTag=1`，`nBlockAlign=nChannels*(bits/8)`，`nAvgBytesPerSec=sampleRate*nBlockAlign`，然后对 `primaryBuffer` 调 `SetFormat`。之后遍历 `soundListHead`，对每个已存在 sound 对象调用 vtable `+0x3C` 触发格式/设备状态刷新。

对象表节点：

| 节点字段 | 建议名 | 含义 |
| ---: | --- | --- |
| `+0x00` | `next` | 双向链表 next |
| `+0x04` | `prev` | 双向链表 prev |
| `+0x08` | `soundHandle` | 对外返回/传入的 sound 对象指针 |

`AddSound` 的资源类型：

| `AddSound` 参数 | 内部类型码 | 对象类型 |
| ---: | ---: | --- |
| `0` | `1` | `KaGuYa::Sound::StaticSound` |
| `1` | `3` | `StreamSoundPolling` |
| 内部分支 | `2` | `StreamSound`，当前导出入口不直接暴露 |

主状态流：

```text
InitSystem
  -> lazy/create g_directSoundSystem
  -> sub_1000FC60
     -> DirectSoundCreate8(NULL, &directSound8, NULL)
     -> IDirectSound8::SetCooperativeLevel(hwnd, DSSCL_PRIORITY)
  -> sub_1000F110 when SetPrimaryBufferFormat is requested
     -> IDirectSound8::CreateSoundBuffer(primary DSBUFFERDESC, &primaryBuffer, NULL)
     -> IDirectSoundBuffer::SetFormat(WAVEFORMATEX)

AddSound
  -> lazy create g_directSoundSystem
  -> 创建 StaticSound / StreamSoundPolling
  -> 插入 g_directSoundSystem->soundListHead
  -> 返回 soundHandle

SoundOpen(handle, ...)
  -> 遍历 soundListHead
  -> 找 node.soundHandle == handle
  -> 调用 handle->vtable[0] 打开资源/流

SoundPlay/Stop/Pause/Close/IsPlay/IsLoop/GetFileName/GetVolume/SetVolume/SetLoop
  -> 遍历 soundListHead
  -> 找 node.soundHandle == handle
  -> 调用对应 sound 对象 vtable method
```

### 7.4 导出函数最终业务名

导出名已经基本是业务名；需要注意的是拼写和层级：

| 导出名 | 建议业务名 | 说明 |
| --- | --- | --- |
| `RenderSceen` | `RenderScreen` | 原导出拼写为 `Sceen`，hook 时必须保留原拼写 |
| `RenderSceenHDC` | `RenderScreenToHdc` | GDI/HDC 输出路径 |
| `UpdatePrimary` | `UpdatePrimarySurface` | 主表面刷新 |
| `GetBits` | `GetPrimaryBits` | 取可访问像素缓冲 |
| `GetPitch` | `GetPrimaryPitch` | 取 pitch/stride |
| `SurfaceLoadALP` | `LoadAlpPixelsToSurface` | AP/AP-0/AP-2/AP-3 已由 Start 侧解析后传入像素 |
| `SurfaceLoadANM` | `LoadAnmPixelsToSurface` | ANM 已由 Start 侧解析后传入像素 |
| `TextCreateFont` | `CreateAnsiTextFont` | 最终走 `CreateFontIndirectA` |
| `TextDraw/TextBlt` | `DrawAnsiText` | 最终走 ANSI/GDI 绘制 |
| `AddSound` | `CreateSoundObject` | 返回后续 sound handle |
| `SoundOpen` | `OpenSoundResource` | 用 handle 定位对象后打开 |
| `SoundPlay/Stop/Pause/Close` | `ControlSoundPlayback` | 播放控制 |

## 8. Hook 覆盖策略

目标是“任意链路可任意 hook”。对这三个 DLL，实际要覆盖四层边界：

### 8.1 Loader / proxy DLL 层

适合场景：想拦截所有调用，且不想关心内部对象偏移。

- 放置同名 `Lib\\Graphics.dll` / `Lib\\RenderDX.dll` / `Lib\\sound.dll` proxy。
- proxy 导出同名函数，内部转发到真实 DLL。
- `Graphics.dll` 会动态装载 `RenderDX.dll` 并 `GetProcAddress("Initialize")`，因此 `RenderDX.dll` 的 proxy 可以截获后端初始化和全部导出。
- `Start.exe` 对 `Graphics.dll` / `Sound.dll` 也是 `LoadLibraryA + GetProcAddress` 模式，因此 proxy DLL 是最稳定的第一层 hook。

### 8.2 导出函数层

适合场景：想 hook 高层业务，不想碰 COM/vtable。

推荐入口：

| 模块 | 高价值导出 |
| --- | --- |
| `Graphics.dll` | `InitSystem`, `SurfaceCreate`, `SurfaceLoadBMP`, `SurfaceLoadALP`, `SurfaceLoadANM`, `SurfaceBlt`, `SurfaceBlendBlt`, `TextCreateFont`, `TextDraw`, `TextBlt`, `RenderSceen` |
| `RenderDX.dll` | `Initialize`, `UpdatePrimary`, `RenderSceen`, `RenderReset`, `Resize`, `GetBits`, `GetPitch`, `SetMagFiltering`, `SetMinFiltering` |
| `Sound.dll` | `InitSystem`, `AddSound`, `SoundOpen`, `SoundPlay`, `SoundStop`, `SoundPause`, `SoundClose`, `SoundSetVolume`, `SoundSetLoop`, `ResetDevice` |

### 8.3 内部对象/vtable 层

适合场景：需要 hook “同一个导出下的不同对象实例”。

- `Graphics.dll`：hook `g_renderArrayBase` 当前对象的 vtable，或 hook `ValidateSurfaceHandle` 后得到的 surface 对象。
- `RenderDX.dll`：hook `Graphics::CD3DDevice::d3dDevice9` 的 COM vtable，可拦截 `Clear/BeginScene/Draw/EndScene/Present/Reset`。
- `Sound.dll`：hook `g_directSoundSystem->soundListHead` 里的 `soundHandle` vtable，可按单个 sound 对象拦截 `Open/Play/Stop/SetVolume`。

### 8.4 系统 API / COM 层

适合场景：想跨模块兜底。

| 子系统 | API/COM hook 点 |
| --- | --- |
| 文本 | `CreateFontIndirectA`, `TextOutA`, `GetTextExtentPoint32A` |
| GDI/DIB | `CreateDIBSection`, `CreateCompatibleDC`, `SelectObject`, `BitBlt`, `StretchBlt`, `DeleteDC`, `DeleteObject` |
| D3D | `Direct3DCreate9`, `IDirect3D9::CreateDevice`, `IDirect3DDevice9::Present/Reset` |
| Sound | `DirectSoundCreate8`, `IDirectSound8::CreateSoundBuffer/SetCooperativeLevel`, `IDirectSoundBuffer::SetFormat/GetVolume/SetVolume/Play/Stop` |
| DLL 链接 | `LoadLibraryA/W`, `GetProcAddress` |

### 8.5 实施顺序

1. 先做 proxy DLL：保证三 DLL 的所有导出可记录、可改参、可转发。
2. 再做内部对象 hook：只在需要分实例控制时启用。
3. 最后做 API/COM hook：用于文本编码、D3D present、DirectSound buffer 这类最终落点。

这样三条链都能闭合：

```text
Start -> Graphics export -> Graphics object -> RenderDX export -> D3D COM
Start -> Graphics export -> Graphics text/surface helper -> GDI
Start -> Sound export -> CDirectSound list -> sound object vtable -> DirectSound COM
```

### 8.6 `params.dat` 默认人名的精确 hook 点

不要把 `WideCharToMultiByte` 全局改成 CP936。v5.8 的逆向链路显示，`params.dat` 的默认自定义人名只在 `Start.exe` 的 `GameSystem` 解析阶段从 UTF-16LE 降到窄字节：

```text
sub_485F60
  -> sub_414C30()->vtable[0]
  -> sub_42F160
  -> sub_417190  // GameSystem
     -> sub_41C6C0(..., this + 0x30, params offset 0x005C) // default first name
     -> sub_41C6C0(..., this + 0x34, params offset 0x0062) // default second name
        -> sub_40CCF0
           -> WideCharToMultiByte(3 /* CP_THREAD_ACP */, ...)

name dialog / runtime update
  -> sub_41F9B0 / sub_422D60
     -> copy GameSystem +0x30/+0x34 to edit fields when name part is not customizable
     -> copy accepted edit fields to GameSystem +0x58/+0x5C

runtime text refresh
  -> sub_424060 / sub_4F6C80
     -> Graphics.dll!SetTextFirstName(GameSystem +0x58)
     -> Graphics.dll!SetTextSecondName(GameSystem +0x5C)
```

字段含义：

| GameSystem offset | 含义 |
| ---: | --- |
| `+0x2C` | name mode flags，来自 params `0x005B` |
| `+0x30` | params 默认 first name，来自 params `0x005C` |
| `+0x34` | params 默认 second name，来自 params `0x0062` |
| `+0x54` | name dialog checkbox/current flag |
| `+0x58` | 运行时 current first name，传给 `SetTextFirstName` |
| `+0x5C` | 运行时 current second name，传给 `SetTextSecondName` |

v5.6 - v5.8 同代 reader 可用的最小修正策略是 hook `Start.exe` 内的 params `string16` reader，只在调用返回地址命中默认人名两个 callsite 时，用 CP936 把 UTF-16LE 字段写入引擎 `char string`。不同游戏/版本地址会漂移，当前已确认矩阵如下：

| 样本 | reader | string assign | default first return | default second return | 备注 |
| --- | ---: | ---: | ---: | ---: | --- |
| v5.8 Hakoniwa | `0x0041C6C0` | `0x00431490` | `0x004174BE` | `0x00417543` | 无 SEH prologue，旧直接入口特征可命中 |
| v5.8_2 OppaiDekai | `0x0040BDE0` | `0x00404770` | `0x0042D119` | `0x0042D14C` | reader 前有 SEH prologue；默认名仍写 `GameSystem +0x30/+0x34` |
| v5.7 | `0x0040BF70` | `0x004049A0` | `0x0042C9F4` | `0x0042CA27` | reader 前有 SEH prologue；默认名仍写 `GameSystem +0x30/+0x34` |
| v5.6 | `0x0040BBC0` | `0x00404B00` | `0x0042A899` | `0x0042A8CC` | reader 前有 SEH prologue；默认名仍写 `GameSystem +0x30/+0x34` |
| v5.5 | `0x0042C1B0` 系资源/反射读取 | `0x00429C00/0x00429C30` 为 size/convert wrapper | resource id `1401` | resource id `1400` | 不是 v5.6+ 的 `(readerState,dst,offset)` reader 签名；当前 hook 工程已走 legacy resource loader + size/convert 双 hook |
| v5.4 | `0x00429DF0` 系资源/反射读取 | `0x00427420/0x00427450` 为 size/convert wrapper | resource id `1401` | resource id `1400` | 同 v5.5，不能复用 v5.6+ reader detour；当前 hook 工程已走 legacy resource loader + size/convert 双 hook |

通用定位不要硬编码上表地址。当前 hook 工程采用和 Kaguya VFS compat hook 相同的扫描思路：

1. 在当前 `Start.exe` 模块内按函数形状定位 params `string16` reader：读取 `u16 byteLen`、复制 UTF-16LE 临时缓冲、再降到引擎 `char string` 的函数。
   - v5.8 Hakoniwa 可从函数入口直接匹配。
   - v5.8_2/v5.7/v5.6 的 reader 前有 SEH prologue，应匹配函数内部“读取 `u16 byteLen` 并组合长度”的核心指令，再回溯到 `55 8B EC` 函数入口。
2. 定位引擎字符串赋值函数，也就是大量 `char string assign(dst, src, size)` 调用使用的函数。v5.8 Hakoniwa 是 `56/57` 保存寄存器形态；v5.8_2/v5.7/v5.6 是 `53/56` 保存寄存器形态。
3. 反扫所有 `call string16Reader`，只接受 call 前出现 `lea reg, [this+0x30]` / `lea reg, [this+0x34]` 且随后在同一短窗口内 `push reg` 的两个调用点，并记录它们的返回地址。注意 v5.8_2/v5.7/v5.6 的 `lea` 与 `push` 中间可能夹着分支和其他参数压栈，不能要求二者相邻。
4. hook `string16Reader` 本体；detour 内用 `_ReturnAddress()` 精确判断是否来自默认姓/名 callsite。命中时直接把 UTF-16LE 字段转为 CP936 并调用引擎 string assign 写回目标字段；不命中则完全走原函数。

这样主修正点仍在 `Start.exe` 内部 params/资源读取链路，而不是全局 `kernel32!WideCharToMultiByte`。扫描失败时应只记录日志并跳过该特化 hook，避免误伤启动链路。

v5.5/v5.4 已按 legacy 路径单独兼容：它们的字符串读入链路走 `FindResourceA`/资源或反射式读取，再分别调用“计算目标 codepage 字节数”和“实际 WideCharToMultiByte 转换”的 wrapper。hook 工程会先在 resource string loader 中记录当前 resource id，只对默认姓名资源 `1401/1400` 同步改写 size wrapper 与 convert wrapper；这样分配长度与实际写入字节数一致，不会出现只 hook convert wrapper 时的缓冲区风险。

`Graphics.dll!SetTextFirstName/SetTextSecondName` 不能作为唯一修正点，但可以作为窄桥和诊断点：v5.8 实测它会先收到原 SJIS 默认名字节，hook 工程会用 Start.exe 侧保存的 CP936 默认名字节替换，防止后续显示链路回退到 SJIS。最终绘制阶段只对命中默认名字节/切片的 `TextOutA/ExtTextOutA` 转为 `TextOutW/ExtTextOutW`，不全局改写日文文本。

如果只 hook `WideCharToMultiByte`，不能只看 API 的直接返回地址；它的直接 caller 是通用 `sub_40CCF0`，会覆盖标题、品牌名、资源名等大量 params 字符串。必须额外用上层返回地址/调用栈识别 `0x004174BE` 和 `0x00417543`，否则等价于全局改码页，容易启动崩溃或污染资源名。

## 9. 当前边界结论

- `Graphics::CRenderGDI` 已逆到 wrapper、外部 `Lib\\RenderGDIp.dll` 装载、内部 DIB surface、HDC 输出和 GDI API hook 点；它不是当前主样本运行路径，但字段级定位已经够做 hook 和调试。
- `RenderGL.dll` 的 `Graphics.dll` wrapper 已逆到装载、`Initialize/GetPitch/GetBits`、primary bits 暴露和 proxy hook 面；当前仓库和 v5.8 样本树没有 `RenderGL.dll` 本体，所以不能给出它内部 OpenGL 设备对象字段。要继续只能补到真实 `RenderGL.dll` 文件后再逆。
- `Sound.dll` 的 `directSound8/primaryBuffer` 已拆到 `IDirectSound8*` 与 `IDirectSoundBuffer*`：创建、协作级别、primary buffer、格式、音量、释放链路均已闭合。按导出、对象表、sound 对象 vtable、DirectSound COM vtable 四层都能 hook。
