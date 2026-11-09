# zh-CN

## Description

TaskHub 把电脑上耗时较长的任务集中到一处，并让你直接控制它们：视频导出、渲染、构建、备份、模型训练、CI 运行、云端 GPU 机器，以及 AI 编程助手替你运行的脚本。

工作方式
程序或脚本通过 127.0.0.1 上的本地 HTTP API、taskhub 命令或随附的 Python 客户端向 TaskHub 报告进度。TaskHub 随后显示：
• 托盘图标，带进度环和活动任务数；
• 面板，列出每个任务的进度、剩余时间、状态和日志，以及任务请求的小组件：折线图、面积图、柱状图、仪表、数值、指示灯、表格；
• 任务自带的控件：按钮，数字、文本或列表输入，暂停、停止和强制结束。你的选择会发回给任务；
• 任务完成、失败或停止汇报时的 Windows 通知；如果任务需要，通知中也带有它的按钮。

“taskhub run -- <命令>”可以包装任意命令行：把输出写入任务日志，从中读取进度，并添加“停止”和“结束”按钮。

设置
面板中的 ⚙ 按钮（或托盘菜单中的“设置”）会在面板旁打开设置，包含以下选项卡：外观、通知和 Telegram、连接器、AI 助手、开机自启以及 API 文档。托盘菜单只保留快捷操作：打开面板、静音、设置、支持、退出。

Claude Code 和 Codex
在“设置 → AI 助手”中可以把 TaskHub 连接到 Claude Code 和 Codex。助手获得 TaskHub 工具（MCP），用来报告它的长任务；当助手完成回复或需要你的许可时，你会收到通知。TaskHub 会把自己的条目写入助手的设置文件，关闭该选项时再将其移除。你还可以开启套餐限额：在面板中显示 Claude Code 和 Codex 套餐限额的已用比例，并在 75% 和 90% 时警告。

连接器（可选，默认关闭）
• GitHub Actions：把你列出的仓库中的 workflow 运行显示为任务：按 job 和步骤显示进度，失败的步骤及其日志末尾，取消和重新运行按钮。
• Runpod 和 Vast.ai：把你的 GPU 机器显示为任务：GPU、每小时价格、已花费金额、运行时长、SSH、负载图表、“GPU 空闲”警告、余额和可用时长（余额不足时警告）、停止 / 启动 / 重启（删除只能在面板中进行，且需输入机器名称）。
• AWS：把 EC2 实例、SageMaker 训练作业和 AWS Batch 作业显示为任务，还有本月费用和预测以及 AWS Budgets，每项都有单独的开关。只读。Cost Explorer 由 AWS 收费（每次请求 $0.01，每月最多约 $1.80）。此连接器根据 AWS 文档构建并通过模拟器测试，尚未在真实 AWS 账户上验证。
每个连接器只有在你开启并输入自己的 API 密钥后才会工作。密钥只保存在 Windows 凭据管理器中，连接器只与其自身服务的 API 通信（api.github.com、api.runpod.io、console.vast.ai、*.amazonaws.com）。

余额保护（可选，在你开启之前处于关闭状态）
适用于 Runpod 和 Vast.ai：在你设定的余额阈值发出警告；到达停止阈值时，先发出带“立即停止”和“推迟”按钮的通知，然后停止运行中的机器。它只停止机器，绝不删除；如果无法读取余额，则不停止任何机器。

Telegram（可选）
连接你自己的 Telegram 机器人，就能在手机上收到同样的通知和每个任务实时更新的进度消息，并在那里点按任务按钮。机器人令牌保存在 Windows 凭据管理器中，消息从你的电脑直接发送到 Telegram 的 Bot API。

隐私
无需账户，没有遥测。API 只监听 127.0.0.1，任务和设置保存在 %APPDATA%\TaskHub。除非你开启 Telegram、某个连接器或 Codex 套餐限额，否则不会有任何数据离开你的电脑。

须知
• 更新通过 Microsoft Store 提供。
• 可在“设置 → 系统”或 Windows“设置 → 应用 → 启动”中关闭开机自启。
• API 文档可从设置页面打开。
• 需要 Windows 10 2004 或更高版本（x64）以及 Microsoft Edge WebView2 Runtime（当前的 Windows 已自带）。
• 界面语言：英语、乌克兰语、西班牙语、德语、法语、意大利语、葡萄牙语、波兰语、简体中文、日语。

## What's new in this version

TaskHub 3.1
• AWS 连接器，默认关闭且只读：EC2 实例、SageMaker 和 Batch 作业、本月费用、Budgets。
• Claude Code 和 Codex 的套餐限额，开启后才生效：显示已用比例，并在 75% 和 90% 时警告。
• 设置在面板旁打开，并带有选项卡。
• 发现助手时，面板会提供 Claude Code 和 Codex 的通知选项，同一件事不再收到两条通知。
• 本地日志更清晰：通知文本和任务名称只在 debug 级别写入。
• 基于 .NET 10 运行。固定的面板会保持在屏幕边缘。
• 已修复：Vast.ai 余额；重启前的 Telegram 按钮可能触发其他任务的按钮。

## Product features

- 托盘图标，带进度环和活动任务数
- 面板为每个任务显示进度、剩余时间、日志、图表、仪表和表格
- 由任务自己声明的按钮和输入框：暂停、停止、修改参数、强制结束
- 任务完成、失败或卡住时的 Windows 通知，带任务按钮
- 本地 HTTP API、taskhub 命令行和 Python 客户端；“taskhub run”可包装任意命令
- 设置在面板旁打开（⚙），带选项卡；托盘菜单提供快捷操作
- Claude Code 和 Codex：TaskHub 工具（MCP）和通知，可在设置中开启
- 可选的 Claude Code 和 Codex 套餐限额：显示已用比例，75% 和 90% 时警告
- 可选 GitHub Actions 连接器：把 workflow 运行显示为任务，可取消和重新运行
- 可选 Runpod 和 Vast.ai 连接器：GPU 机器、花费、余额、GPU 空闲警告、停止和启动
- 可选的 AWS 连接器，只读：EC2 实例、SageMaker 和 Batch 作业、费用、Budgets
- 可选余额保护：余额不足时警告，并停止（绝不删除）运行中的机器
- 可选 Telegram 机器人：在手机上接收通知、进度并使用任务按钮
- 无需账户，无遥测；API 密钥只保存在 Windows 凭据管理器中；API 只监听 127.0.0.1
- 10 种界面语言

## Short description

查看并控制电脑上耗时较长的任务：进度、图表、通知以及任务自带的按钮。可通过本地 API 或 taskhub 命令配合任意脚本使用，支持 Claude Code 和 Codex，还可选择连接 GitHub Actions、Runpod、Vast.ai 和 AWS。
