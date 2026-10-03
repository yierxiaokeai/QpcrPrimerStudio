# qPCR Primer Studio

用于 **SYBR Green RT-qPCR 基因表达实验**的 Windows 桌面引物工作台，支持多基因设计、真实 Primer3 计算、本地 BLAST 特异性检查、引物筛选、工程保存和 CSV/Excel 导出。

本仓库保存当前源码、测试和必要的构建配置与脚本。计算引擎、运行库、数据库、实验数据及构建产物在本地准备，不随源码提交。

## 产品定位

围绕“导入靶标 → 设置条件 → 设计候选 → 检查特异性 → 筛选导出 → 实验验证”组织工作，主要使用转录本/cDNA 模板，分别记录转录本覆盖、结构风险和潜在基因组 DNA 扩增风险。

- 单基因与批量设计：引物对、单引物、固定一端寻找配对；支持手工编辑和重新评估。
- 智能推荐参数：调用实际 Primer3 试算，展示条件、候选数量及依据，确认应用后正式设计。
- 工程共用参数：统一 Tm、GC、引物长度、产物长度等搜索条件，保留各基因区域与反应体系。
- 质量与结构：显示综合评分、Tm、GC、发卡、二聚体、错配及明确的排除原因。
- 特异性：本地 BLAST 检查转录本及基因组数据库，保留原始证据和数据库、查询身份。
- 多转录本与参考注释：CDS/UTR、共有区域、连接位点、变异和 gDNA 风险分析。
- 保存与恢复：勾选自动保存，按基因保留模板和手工 F/R 原稿，支持备份、修订和保存冲突保护。
- 导出：普通导出默认仅勾选；智能导出在质量、目标结合错配及发卡筛选后，每个基因保留最高评分的一对。

## 技术与运行

- Windows 桌面：C# + .NET 10 + WPF，采用 MVVM 分离界面与业务。
- 引物设计与热力学：复用 Primer3，以独立进程适配器调用官方工具。
- 特异性：本地 BLAST+ 与成对扩增产物分析，分别记录转录本和基因组检查结果。
- 工程结果：JSON 项目保存原始输入、候选、实际参数、来源指纹及检查证据；SQLite 保存参考索引和引物库。
- 科学格式：.NET Bio 导入 FASTA/GenBank；GMOD GFF/VCF 库通过独立 Node 进程解析注释与变异；CSV/XLSX 使用成熟库。

## 从源码运行

需要 Windows x64、PowerShell 7、Git 和 **.NET SDK 10.0.401**；SDK 版本由 `global.json` 固定。工具准备脚本下载 Primer3 2.6.1、BLAST+ 2.14.0+、Node 24.19.0 及所需解析依赖，不要求全局安装 Node 或 Python。首次准备工具和还原依赖需要联网。

在 PowerShell 7 中克隆并准备：

```powershell
$ErrorActionPreference = 'Stop'
git clone https://github.com/yierxiaokeai/QpcrPrimerStudio.git
Set-Location QpcrPrimerStudio
$env:DOTNET_CLI_HOME = Join-Path $PWD '.cli'
$env:NUGET_PACKAGES = Join-Path $PWD '.packages'
pwsh -File tools/setup-tools.ps1
dotnet restore QpcrPrimerStudio.sln --locked-mode --configfile NuGet.Config
dotnet run --project src/QpcrPrimerStudio.Desktop --no-restore
```

工具安装在 `tools/primer3/`、`tools/blast/` 和 `tools/reference/` 的本地运行目录；下载、测试和导出证据写入 `artifacts/`。这些生成内容已被 `.gitignore` 排除。

## 使用

1. **添加靶标**：粘贴转录本/cDNA，或导入 FASTA/GenBank，填写唯一靶标 ID 并保存序列。
2. **设置条件**：点击 Search，选择引物对、单引物或固定一端配对，设置区域与长度；可试算“智能推荐参数”，确认后应用。
3. **选择单基因或批量设计**：批量对工程中多个靶标分别生成候选，保留每个任务的状态、参数与结果；失败靶标可重试。勾选工程共用条件可统一搜索数值。
4. **检查候选**：查看评分、Tm、GC、发卡、产物和错配；按实际退火条件排除高风险发卡。在“参考与特异性”绑定数据库并运行检查。
5. **保存并设计下一基因**：勾选或取消勾选候选会自动保存整个工程。首次产生本机草稿，已有工程写回原路径；点击“添加序列”继续新靶标，已有设计保留。
6. **查看与返回**：左侧选择基因同步模板与该基因当前结果；没有设计时显示空表。点击“候选表”或“结果”返回列表，历史任务标明旧模板。
7. **导出与实验记录**：普通导出默认仅勾选；批量完成后可选择“智能导出 · 每基因一对”，预览保留项、缺失基因和理由，再导出。可记录效率、R²、熔解曲线、NTC、−RT 等实验信息。

简单模式保留常用条件与评分；专业模式展开完整设置和详情。切换模式保留结果。F1 或“帮助”打开内置离线说明，源文档位于 [Help](src/QpcrPrimerStudio.Desktop/Help)。

### 保存和导出规则

- 各基因分别保存模板与手工 F/R 原稿，包括尚未完成的输入；新基因原稿可通过“添加序列”恢复。保存工程保留原稿，保存序列及计算时校验序列。已有 ID 不会覆盖其他基因。
- 自动保存显示实际写盘状态，关窗等待保存完成；跨窗口保存冲突保留独立副本。近期压缩备份最多 10 份、50 MB，较早记录完整归档，历史总量会继续增长。
- 普通导出默认覆盖工程内各设计任务的勾选候选，并排除已应用的发卡筛选项。人工勾选的质量拒绝项仍可用于复核，导出列明质量状态、原因和模板版本；CSV/XLSX 覆盖旧文件前保留备份。
- 智能导出按钮与“仅导出勾选项”同排。检查当前模板及证据归属，排除质量拒绝、无有效评分、目标结合错配/插缺或无法核对、非靶向产物及高风险发卡后，每基因选最高评分一对。已绑定数据库须完成特异性检查，每个预期转录本须有零错配、无插缺的完整 F/R 产物；未绑定数据库时仅确认模板结合并明确标注数据库未检查。预览和导出记录列出排除数量与原因。明确 GeneId/参考映射时合并同基因转录本；缺少映射时按靶标 ID 分组。原候选与手动勾选保留。
- 覆盖参考转录本 ID 与特异性数据库预期 ID 分别设置。更改数据库、参考、靶标信息或引物查询后，过期证据会要求重新设计或检查；旧版缺少查询绑定的 BLAST 报告需要重查。

所有坐标显示和存储为 1-based 闭区间，Primer3 输入适配为 0-based。引物合成序列始终按 5′→3′ 显示。

## 验证与本地构建

完成工具准备和依赖还原后，在仓库根目录运行集成检查：

```powershell
$ErrorActionPreference = 'Stop'
dotnet run --project tests/QpcrPrimerStudio.Checks --no-restore -- $PWD.Path
```

截至 2026-10-03，当前实现的 **444 项集成检查通过**，覆盖真实 Primer3/ntthal/BLAST、坐标与方向、参考注释、质量及证据归属、项目/备份、CSV/XLSX、原稿恢复、保存冲突、取消和实际 WPF 控件。测试使用固定种子合成模板及独立本地目录；100 条各 600 nt 模板批量检查为 99 成功、1 个故意无效的参数局部失败。已通过自包含 EXE 的隔离运行验证及普通 CSV 写入进程终止保护验证。

生成本地自包含 Windows x64 单文件：

```powershell
$ErrorActionPreference = 'Stop'
pwsh -File tools/build.ps1
```

脚本先运行集成检查，再构建并验证 EXE，输出到 `artifacts/single-file/`，已有包移入本地归档。单文件包含 .NET Windows Desktop 运行时、计算引擎及参考解析环境，启动时自动解压；体积包含这些离线运行依赖。此命令仅生成本地产物，不上传或创建 GitHub Release。

### 验证边界

综合评分及结构风险为公开、可追溯的启发式指标；推荐参数试算不执行数据库特异性检查。未检查、搜索不完整或证据过期不会被标为通过，已检测的非靶向风险保留排除原因。

当前验证集中在开发机、合成数据与真实计算引擎。真实植物大型数据集、大家族/假基因灵敏度、干净电脑、多显示器/多 DPI、真实断电及湿实验仍需验收；尚无自动完整 MIQE 合规判定。订购与实验前需核查目标转录本、数据库、产物及实验条件，并实际验证扩增效率、熔解曲线和产物身份。

## 源码结构

| 路径 | 职责 |
|---|---|
| [src/QpcrPrimerStudio.Core](src/QpcrPrimerStudio.Core) | 引擎适配、质量与证据、参考区域、保存、备份和导出 |
| [src/QpcrPrimerStudio.Desktop](src/QpcrPrimerStudio.Desktop) | WPF/MVVM 工作台、导航、编辑、自动保存与内置帮助 |
| [tests/QpcrPrimerStudio.Checks](tests/QpcrPrimerStudio.Checks) | 实际引擎、持久化、故障及界面集成检查 |
| [tools/setup-tools.ps1](tools/setup-tools.ps1) | 准备固定版本计算引擎与解析环境 |
| [tools/build.ps1](tools/build.ps1) | 本地验证、构建和发布目录归档 |
| [tools/verify-package.ps1](tools/verify-package.ps1) | 独立目录下验证自包含 EXE |
| [tools/reference](tools/reference) | GFF3/VCF 的 JS 解析入口及 npm 锁定配置 |

应用图标和内置帮助是程序构建资源，随源码保留。运行下载、缓存、数据库、工程文件、审核记录及发布包均留在本地。

## 依赖与许可

NuGet 依赖锁定在各项目的 `packages.lock.json`；发布依赖使用 `packages.publish.lock.json`；npm 依赖锁定在 `tools/reference/package-lock.json`。

| 组件 | 当前版本 | 来源与许可 |
|---|---|---|
| Primer3 / ntthal | 2.6.1 | [primer3-org](https://github.com/primer3-org/primer3)，GPL-2.0-or-later，独立进程调用 |
| BLAST+ | 2.14.0+ | [UGENE 53.1 工具包](https://github.com/ugeneunipro/ugene/releases/tag/53.1)所含 NCBI 程序；保留 NCBI 及第三方说明 |
| Node | 24.19.0 | Node.js 官方 Windows 运行时，MIT 及其第三方许可 |
| GMOD GFF / VCF | 2.1.0 / 7.2.0 | npm，MIT |
| NetBio.Core | 3.0.0-alpha | .NET Bio，Apache-2.0；固定旧版，格式适配有兼容检查 |
| CommunityToolkit.Mvvm | 8.4.2 | Microsoft，MIT |
| Markdig | 1.4.0 | BSD-2-Clause |
| Microsoft SQLite / INI | 10.0.12 | Microsoft，MIT |
| ClosedXML | 0.105.1 | MIT |
| CsvHelper | 33.1.0 | Apache-2.0 / MS-PL |

工具脚本同时准备 Primer3 所需 MSYS2 运行库及许可证；这些下载组件各自的许可适用于相应组件。项目自身许可证尚未指定。
