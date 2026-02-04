# **🍞 AdvHD Engine WS2 Tools (Support Ver2.1.0.0+)**

> - **Original project by jyxjyx1234: https://github.com/jyxjyx1234**  
> - **Modified to support the AdvHD engine v2.1.0.0**  
> - **Requirement: Python 3.10+**
> - **Please note that my skills are limited, so please excuse any potential errors**

## **♯ How to use**

 - arc.py : pack/unpack AdvHD's .arc archive or dec/enc .ws2 files
 - decompile.py : decompile .ws2 files into clear .txt files
 - dump.py : dump names & messages from .txt files into .json files
 - trans.py : inject translated .json files into .txt files and compile them into .ws2 files

## **♯ Notes**

 - oplist.json : as the name suggests, includes the opcode list
 - namedict.json : the namedict that code dumped / the default namedict which will be injected
 - trans.py : 
 > - The program recompiles the decompiled text after backfilling the translation; therefore, the final script is determined by both the translated text and the decompiled text.
 > - If dump.py has omissions that exist in the decompiled text, you can directly modify the decompiled text to supplement them.

## **♯ Changelog**

> Ver1.0.0 : Base version compatible with Ver2.1.0.0 AdvHD Engine (U16LE text)