# 课件续播内核来源

`src/SlideSync/` 下的这批文件移植自 **ClassSlideSync（课件续页）** 插件的 `Core/`：

- 原项目：https://github.com/KKT446/ClassSlideSync
- 许可证：MIT License
- 版权：Copyright (c) 2026 kkt446

移植时只做了三件事：改命名空间、去掉 ClassIsland 课表/自动化耦合、把台账日志接到
ClassSoftwareHub 的 `AppLog`。台账、放映检测、WPS 死引用恢复、希沃 MCP 桥、
课件改动后的页码对齐等逻辑保持原样。

```
MIT License

Copyright (c) 2026 kkt446

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```