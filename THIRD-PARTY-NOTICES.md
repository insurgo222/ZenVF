# Third-Party Notices / 第三方组件声明

ZenVF includes or links against the following third-party components. The original licenses are reproduced under `licenses/` in the source tree and in the published `dist/licenses/` directory.

## LibreHardwareMonitorLib 0.9.6

- 用途：读取 AMD CPU 温度、封装功耗、有效频率、VID 以及 PPT/TDC/EDC 限制。
- 许可证：Mozilla Public License 2.0 (MPL-2.0).
- 项目主页：<https://github.com/LibreHardwareMonitor/LibreHardwareMonitor>
- 版权：LibreHardwareMonitor contributors.
- 分发方式：通过 NuGet 引用未修改的程序集。
- 许可证全文：`licenses/MPL-2.0-LibreHardwareMonitor.txt`.

MPL-2.0 允许以较大作品的一部分分发该库。ZenVF 未修改 LibreHardwareMonitorLib；如需要该库源码，请使用上方项目主页。

## OxyPlot / OxyPlot.Wpf 2.2.0

- 用途：V/F 曲线绘制与 PNG 导出。
- 许可证：MIT License.
- 项目主页：<https://github.com/oxyplot/oxyplot>
- 版权：Copyright (c) 2014 OxyPlot contributors.
- 分发方式：通过 NuGet 引用未修改的程序集。
- 许可证全文：`licenses/MIT-OxyPlot.txt`.

## PawnIO 2.1

- 用途：用户态程序通过 PawnIO 设备加载 RyzenSMU 模块并访问 AMD SMU。
- 许可证：GNU General Public License version 2，附 PawnIO 特有的设备 IOCTL 链接例外。
- 项目主页与源码：<https://github.com/namazso/PawnIO>
- 版权：Copyright (C) 2026 namazso.
- 分发方式：随程序附带未修改的官方安装包 `PawnIO_setup.exe`（文件版本 2.1.0.0）。
- 许可证全文：`licenses/GPL-2.0-PawnIO.txt`.

PawnIO 的链接例外允许独立程序仅通过设备 IOCTL 接口与 PawnIO 通信，而不使该程序整体受 GPL 约束。ZenVF 不包含 PawnIO 的驱动源码，也不静态链接 PawnIO。PawnIO 的完整源码可从上述项目主页获取；如需要，也可通过项目 issue 联系以获取对应源码副本。

## RyzenSMU.bin (PawnIO module)

- 用途：在 PawnIO 中执行 AMD RSMU 邮箱命令。
- 来源：LibreHardwareMonitor 项目中的 PawnIO 资源模块。
- 许可证：GNU Lesser General Public License version 2.1 (LGPL-2.1).
- 项目主页：<https://github.com/LibreHardwareMonitor/LibreHardwareMonitor>
- 分发方式：以未修改的二进制数据嵌入 ZenVF 程序集，运行时加载到 PawnIO。
- 许可证全文：`licenses/LGPL-2.1-PawnIO-Modules.txt`（同时随输出附带 `COPYING-PawnIO-Modules.txt`）。

该模块是独立、可替换的数据模块，通过 PawnIO 的公开加载接口使用。其源码随 LibreHardwareMonitor 项目提供。

## .NET 10 / WPF

- 用途：运行时与桌面 UI 框架。
- 许可证：MIT License.
- 项目主页：<https://github.com/dotnet/runtime>
- 仅在 self-contained 发布版本中随程序分发 .NET 运行时。许可证文本由发布目录中的 .NET 组件附带。

## 关于 SMU 命令与公开资料

ZenVF 的 SMU 命令号、寄存器字段和传感器使用方式参考了公开的 AMD SMU 文档、社区逆向资料以及 LibreHardwareMonitor 的公开实现。项目不包含、不链接、也不复制 ZenStates-Core 的源代码。AMD、Ryzen、Precision Boost Overdrive 是 Advanced Micro Devices, Inc. 的商标。

## 参考与致谢

- SkatterBencher / Shamino 的 LoopFmax 工作流与 V/F 曲线展示方式：
  <https://skatterbencher.com/ryzen-loopfmax/>
- LibreHardwareMonitor 与 PawnIO 社区对 AMD SMU 访问方式的公开整理。
