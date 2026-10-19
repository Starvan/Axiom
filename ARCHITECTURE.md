# 架构地图

产品范围见 [V1](docs/product/v1-scope.md)，当前可用性与交接见 [当前状态](docs/product/current-state.md)，功能代码入口见 [定位表](docs/development/code-map.md)。本文按任务查阅，不是新对话必读全文；只导航稳定技术边界，不重复进度。

```text
WinUI（可退出） ── Named Pipes ── Service 角色
                                  ├─ Engine：规则 / 仲裁 / 执行授权
                                  ├─ SQLite：配置单写入
                                  └─ Named Pipes ── Session Agent
                                                      └─ 会话观察与动作
```

开发预览用当前用户管理员后台宿主承载 Service 角色，尚非正式 Windows Service。短命 Axiom.exe 提权后引导启动；登录使用当前用户最高权限交互任务。三角色不等于恰好三个 PID；UI 可以为零。权限与原生进程通知见 [ADR 0009](docs/adr/0009-native-process-events-elevation.md)，UI 职责见 [UI 架构](docs/product/workbench-ui.md)。

配置辅助工具由 Host 的 `WorkbenchUtilities` 受权处理：`SteamToolManager` 协调进程组与工具设置的 SQLite 单写入，Steam 元数据发现与缓存；启用后后台启动扫描一次并更新固定组及长期排除项，零 UI 也工作。它不执行游戏启动、不注册或修改自动化规则，也不替代 Agent 的进程观察/系统动作；UI 仅走同用户管道。Steam 文件通知只标记缓存失效，无计时扫描；入口按 [定位表](docs/development/code-map.md) 查相关行。

托盘动作由 `TrayPlugin → TrayCapabilityProvider → WindowsTrayActions` 接入已有 Engine 命令链路；完整路径多选、只读系统身份记录、按需共享通知，一次性移除图标而非持续 Desired State。未公开记录格式和恢复限制见 [ADR 0010](docs/adr/0010-tray-icon-discovery.md)，不增加注入或轮询。

任务栏托盘管理是工具箱的直接交互入口：Host持有专属内部任务作为唯一配置，普通规则接口不展示或修改它；启动一次与持续模式共用Engine命令链路。持续模式仅在启用时订阅Agent的目标托盘变化，原生观察只报告经完整路径核对的活动身份，Host合并通知再执行；关闭释放订阅，不承诺恢复已移除图标。UI不直调Provider，不使用周期扫描；本轮状态与实测边界见当前交接。

画面明亮面积由`ScreenContentPlugin → ScreenContentCapabilityProvider → WindowsScreenContentCapture`按需观察SDR主屏，GPU64×36采样归约后仅回读4字节，启用时有桌面更新最多每秒四次；只发状态与估算比例，不保存/发送画面。多个需求共享DXGI采集，退出最后需求才释放。Agent管理检测时段及稳定期限，HDR/会话/电源变化经通知停采或恢复；独立颜色通知在HDR停采后仍可感知恢复。亮度写入沿用原Engine/恢复租约，必要有限等待边界见 [ADR0011](docs/adr/0011-screen-content-brightness.md)。

后台自身生命周期检测由 Host 提供输入（`AxiomStartupPlugin` / `PluginTaskRuntime`），不要求 Agent 反向检测 Host 进程。开机去重在现有任务库原地持久化；Agent 就绪后经 Engine 的命令路径执行，UI 仅渲染声明字段。系统观察及程序操作仍位于 Agent。

| 改动 | 只读相关规范 |
|---|---|
| 项目依赖、职责 | [overview](docs/architecture/overview.md) |
| 发布布局、运行依赖、安装卸载 | [分发说明](docs/development/distribution.md)；三角色共用 app 目录，启动器独立 Native AOT，旧预览布局兼容 |
| 状态与执行确认 | [state-model](docs/architecture/state-model.md) |
| 规则/冲突/时间 | [rule-engine](docs/architecture/rule-engine.md) |
| 接入条件或动作 | [任务接入](docs/development/task-authoring-contracts.md)、[capability-provider](docs/architecture/capability-provider.md) |
| 跨进程请求/身份 | [ipc](docs/architecture/ipc.md)、[security](docs/architecture/security.md) |
| 应用快捷键与功能入口 | [快捷键 contract](docs/development/keyboard-shortcuts.md) |
| 数据保存与恢复 | [persistence](docs/architecture/persistence.md) |
| CPU / 调度 / 订阅 | [performance-model](docs/architecture/performance-model.md) |
| 轻量模式 / 手动接管 | [lightweight-mode](docs/architecture/lightweight-mode.md)、[manual-override](docs/architecture/manual-override.md) |
| 验证策略 | [testing-strategy](docs/architecture/testing-strategy.md) |
| 未来远程边界（当前不做） | [remote-control-boundary](docs/architecture/remote-control-boundary.md) |

类型与当前实现细节按需读 [核心运行时](docs/development/rule-runtime-contracts.md)、[生命周期](docs/development/runtime-lifecycle-contracts.md)、[HDR 预览](docs/development/game-hdr-preview-contracts.md)。旧服务观察和文字草稿 contract 仅在维护对应代码时读。不得重走“先 skeleton/fake 再允许真实功能”的旧阶段。
