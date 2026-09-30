# 第三方组件许可声明 / Third-Party Licenses

本项目（**n8n 便携整合包 / n8n Portable Gv**）中，**仅启动器（n8n Launcher Gv）为本人原创作品，采用 MIT 协议开源**（详见 [LICENSE](./LICENSE)）。

整合包内集成的 n8n、Node.js、Python（WinPython 发行版）、FFmpeg 等第三方软件，**版权均归各自作者所有，并遵循其原有的许可协议**。本项目未对上述任何组件的许可条款作出修改、扩展或限制。

> 本项目为**非商业性质**，永久免费提供，不进行任何形式的转售，亦不以托管服务（SaaS）形式向第三方提供 n8n。

各组件的许可证原文均**随整合包完整附带**，位于其各自的安装目录内，未被删除或改动。

---

## 组件清单

### 1. n8n

| 项目 | 内容 |
|---|---|
| 许可协议 | **Sustainable Use License**（fair-code，非 OSI 开源协议） |
| 版权归属 | n8n GmbH |
| 官方主页 | https://n8n.io |
| 源码仓库 | https://github.com/n8n-io/n8n |
| 许可原文位置 | 整合包内 `app/node_modules/n8n/LICENSE.md`、`LICENSE_EE.md` |
| 权威版本来源 | 整合包内 `app/node_modules/n8n/package.json` 的 `version` 字段 |
| 本文档撰写时随附版本 | v2.40.7 |

**关于企业版（Enterprise）文件的声明：**

n8n 官方 npm 发布包中，文件名含 `.ee.` 或目录名含 `.ee` 的源码文件，**不属于 Sustainable Use License 的授权范围**，其使用需持有有效的 n8n Enterprise License（详见 `LICENSE_EE.md`）。

本整合包仅原样转发 n8n 官方公开发布于 npm 的安装包，**未解锁、未绕过、未破解任何许可校验，亦不提供或主张任何企业版授权与功能**。相关企业版功能仍需使用者自行向 n8n 官方取得有效授权后方可启用。

**关于 Sustainable Use License 的使用限制（请使用者注意）：**

- 仅可将 n8n 用于**内部业务目的**；
- **不得**转售 n8n，或将其作为托管服务提供给第三方；
- 分发时**必须保留** n8n 的版权声明与许可声明。

---

### 2. FFmpeg

| 项目 | 内容 |
|---|---|
| 许可协议 | **GNU General Public License v3 (GPL v3)** |
| 版权归属 | the FFmpeg developers |
| 官方主页 | https://ffmpeg.org |
| 源码仓库 | https://github.com/FFmpeg/FFmpeg |
| 源码下载 | https://ffmpeg.org/download.html |
| 构建提供方 | Gyan Doshi — https://www.gyan.dev/ffmpeg/builds/ |
| 构建类型 | `essentials_build`（Windows x64，静态链接） |
| 许可原文位置 | 整合包内 `runtime/ffmpeg/LICENSE` |
| 本文档撰写时随附版本 | `2026-09-24-git-5253641e62`（对应上游 commit `5253641e62`） |

**关于 GPL 与源码获取的说明：**

本整合包所附 FFmpeg 构建启用了 `--enable-gpl --enable-version3` 编译选项（包含 libx264、libx265、libxvid 等 GPL 组件），因此该二进制文件整体受 **GPL v3** 约束。

依据 GPL 条款，现明确提供其完整源码的获取途径：

- 上游源码仓库：https://github.com/FFmpeg/FFmpeg
- 对应版本 commit：`5253641e62`
- 官方源码发布页：https://ffmpeg.org/download.html
- 本二进制构建的完整编译参数，可在整合包内执行 `runtime/ffmpeg/bin/ffmpeg.exe -version` 查看

本项目**未对 FFmpeg 源码作任何修改**，所附二进制为上述第三方构建方公开发布的未改动版本。

---

### 3. Node.js

| 项目 | 内容 |
|---|---|
| 许可协议 | **MIT License**（其内含第三方依赖遵循各自协议） |
| 版权归属 | Node.js contributors、OpenJS Foundation |
| 官方主页 | https://nodejs.org |
| 源码仓库 | https://github.com/nodejs/node |
| 许可原文位置 | 整合包内 `runtime/node/LICENSE` |
| 本文档撰写时随附版本 | v24.14.1 |

Node.js 随附的 `npm`、`corepack`、`asar` 等附属包，其许可原文分别位于 `runtime/node/node_modules/` 下各自目录内。

---

### 4. Python（WinPython 发行版）

整合包内的 Python 采用 **WinPython** 免安装发行版，其中包含 CPython 解释器本体与一套打包工具。二者版权与许可不同，分列如下。

**4.1 CPython（解释器本体）**

| 项目 | 内容 |
|---|---|
| 许可协议 | **PSF License Agreement**（Python Software Foundation License） |
| 版权归属 | Python Software Foundation |
| 官方主页 | https://www.python.org |
| 源码仓库 | https://github.com/python/cpython |
| 许可原文位置 | 整合包内 `runtime/python/python/LICENSE.txt` |
| 权威版本来源 | 整合包内执行 `runtime/python/python/python.exe --version` |
| 本文档撰写时随附版本 | 3.13.13 |

**4.2 WinPython（发行版打包）**

| 项目 | 内容 |
|---|---|
| 许可协议 | **MIT License**（WinPython License Agreement） |
| 版权归属 | Pierre Raybaut (2012)、WinPython team (2016+) |
| 官方主页 | https://winpython.github.io |
| 源码仓库 | https://github.com/winpython/winpython |
| 许可原文位置 | 整合包内 `runtime/python/license.txt` |

WinPython 自身声明：其收录的各组件"均按从版权持有人处获得的原样分发，遵循各自的版权与许可"。本项目未对 WinPython 的打包结构或其中任何组件作出修改。

整合包内预装的 Python 第三方库（如 `yt-dlp`、`requests`、`browser-cookie3` 等）均遵循其各自的开源许可协议，许可原文位于 `runtime/python/python/Lib/site-packages/` 下各自的包目录内。

---

### 5. 思源黑体 / Source Han Sans

| 项目 | 内容 |
|---|---|
| 许可协议 | **SIL Open Font License 1.1** |
| 版权归属 | Adobe Systems Incorporated |
| 源码仓库 | https://github.com/adobe-fonts/source-han-sans |
| 许可原文位置 | 启动器源码内 `Assets/Fonts/LICENSE-OFL.txt` |

该字体依 SIL OFL 1.1 允许自由使用与再分发，本项目将其嵌入启动器界面用于中文显示。

---

## 关于版本号

上表中的"本文档撰写时随附版本"仅为**参考快照**。整合包会随维护更新组件版本，届时该快照可能与实际不一致。

**判断实际版本请以整合包内的权威来源为准**（如 `package.json`、`ffmpeg.exe -version` 等，已在各组件条目中标明）。

组件的**许可协议类型与源码获取地址不随版本变化**，故本文档的合规声明持续有效。

---

## 声明范围

本文档所列声明**仅用于说明第三方组件的归属与许可**，不构成对任何第三方软件许可条款的解释、修改或法律建议。

使用本整合包即表示您同意遵守上述各第三方组件各自的许可协议。如对具体条款存疑，请以各组件官方发布的许可原文为准。

---

*最后更新：2026-08-10*
