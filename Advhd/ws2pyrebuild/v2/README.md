# **🍞 Advhd Engine'ws2 tools match Ver2.1.0.0 or 2.1.0.0+**

> - **Original project by jyxjyx1234: https://github.com/jyxjyx1234**  
> - **Modified to support the Advhd engine v2.1.0.0**  
> - **Please note that my skills are limited, so please excuse any potential errors**




## **♯ Ws2Explorer**

 - 这是最普遍也是兼容性最好的一个advhd工具(推荐)
 - 具体使用请用命令行查看，现已扩展多种功能
 - 值得注意的是，Rio.arc、Rio1.arc与Rio文件夹的 *.ws2 文件是经过加密的，而GARbro提取的话会自动对其进行解密。
 - 如果提不出文本可以用流风破解破解一下exe
 - 注意，本工具产生的的双行文本主要有Name、Msg、Choice三类。另外，本工具产生的 人名表.txt 无用，人名取决于Name行人名
 - 使用UTF-8需要启用-add参数，通过其针对选项末尾处自动追加2个半角空格以规避某些BUG

### 更新日志

> Ver1.0.0 : 基本完成重构工作，添加编码参数，使GBK/U8的提取/回封功能实现  
> Ver1.1.0 : 追加对 *.ws2 文件的解加密功能  
> Ver1.2.0 : 追加解封包 *.arc 封包的功能，添加可选参数-dec/-enc，可实现无解密解包  
> Ver1.2.1 : 追加了针对UTF-8回封的Choice特殊处理  
> Ver1.2.2 : 优化双行文本，分离Name与Msg  
> Ver1.2.3 : 增加可选参数-add，可自由选择是否开启对Choice的特殊处理  
> Ver1.2.4 : 修复了复合ID处理问题、空行问题、换行符处理问题  




## **♯ Ws2Explorer GUI**

 - 在ws2Parse失效或出现BUG时使用，自行编译对Ws2Explorer.Gui编译即可
 - 使用时请从 View - Show Terminal 调出终端，使用 help 查看具体指令
 - 回注时可以从 Edit - Encoding 选择编码
 - 导出文本中的【】请勿翻译

### 更新日志

> Ver1.0.0 : 添加编码参数，使U8的回封功能实现，并且使得导出时可导出人名  
> Ver1.1.0 : 扩展 insert_text 与 insert_text_folder 的参数，可设置导出文件夹




## **♯ ws2VNTFuck**

 - 由VNT对应代码采用C++重构后的工具，兼容性最低，但是某些时候可以解决一些前两个工具的BUG(例如Guilty的七人孕女的选项BUG)

### 更新日志

> Ver1.0.0 : 初步重构完成，优化双行文本提取，添加了编码参数  
> Ver1.0.1 : 追加了针对UTF-8回封的Choice特殊处理，增加可选参数-add

# 2、图片工具:

> - AdvhdPictureTool，目前可处理png、MOS、PNA图片或图包
> - 这个引擎的视频是长得像OP.dat、ED.dat的文件，将其后缀改成 .wmv 即可查看
