# assets —— 图标资源

| 文件 | 说明 |
|---|---|
| `DshLauncher.ico` | 托盘图标，**深色任务栏**用（白色字形） |
| `DshLauncher_light.ico` | 托盘图标，**浅色任务栏**用（黑色字形） |
| `original.svg` | 上面两个图标的矢量来源 |

## 谁在用

`DshLauncher\build.cmd` 把两个 `.ico` 以 `/win32icon:` 和 `/resource:` 两种方式
嵌进 `DshLauncher.exe`；程序启动时读
`HKCU\...\Themes\Personalize\SystemUsesLightTheme` 选一套，所以不管任务栏是深是浅，
托盘图标都看得见。

`DshLauncher\DshLauncher\DshLauncher.csproj` 也按 `..\..\assets\` 引用它们。

**移动或改名这两个文件会直接让托盘编译失败** —— `build.cmd` 有 pre-flight 检查，
找不到就报错退出，不会产出一个没有图标的 exe。

## 格式要求

图标必须是**经典 DIB 格式**（16/32/48 三档 DIB + 256 PNG），且不超过 48px 的 DIB 项。
不符合的话 Windows 会静默不显示托盘图标 —— 具体原因见
[../DshLauncher/README.md](../DshLauncher/README.md) 的「三个必须知道的实现约束」。

## 重新生成

两个 `.ico` 由 `original.svg` 一次性生成（PowerShell + `System.Drawing` 从 SVG
光栅化，16/32/48 三档 DIB + 256 PNG 混排）。格式约束见
[../DshLauncher/README.md](../DshLauncher/README.md) 的「三个必须知道的实现约束」。
日常改动不需要重新生成。
