# ADR 0011：低频画面明亮面积检测

2026-10-17，Accepted（实现与数值验收另记）。用户要求大面积浅色画面自动降亮，低开销优先，可限定夜间；HDR开启时不需要此功能。

## 决策

- 检测主屏画面中明亮采样点的面积比例，不只数纯白，不代表环境照度、背光百分比或真实nits。采用固定网格、相对亮度阈值、进入/退出面积滞回和稳定时间（0秒可在第一份有效样本立即改变状态），复用已有亮度恢复Desired动作，不逐帧写亮度。
- 画面留在GPU，GPU归约后CPU只读小统计；不把整屏复制到托管内存、不编码/保存/上传画面，日志和管道只传布尔与解释性比例。无依赖时释放，多个消费者共享必要采集。
- 自动检测HDR；开启、未知、锁屏、屏幕关或设备失效时暂停采集并给Unknown，释放亮度租约遵守原保护。关闭HDR/恢复后靠系统事件重建，不要求手工重启规则。可选HH:mm时段在Agent最近边界启停采集，时段外Known false；不能只用Command执行门禁冒充停采。
- AdvancedColorInfoChanged也可能由亮度/white-level元数据变化引起，仅SDR/WCG/HDR模式实际变化才使采集失效，避免自身亮度动作形成释放/恢复反馈；拓扑刷新仍强制重绑。依据见 [DisplayInformation互操作说明](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.display.interop/nf-windows-graphics-display-interop-idisplayinformationstaticsinterop-getforwindow)。

## API选择与必要例外

最终采用DXGI桌面复制，不保留WGC正式实现。WGC原型支持系统MinUpdateInterval=1秒及GPU64×36归约/4字节回读；停用后无新帧。当前便携版无包身份，取消其系统边框需授权及manifest能力，不能仅设IsBorderRequired=false就声称无边框。真正DXGI原型已核对源码并运行，收到约1fps面积统计，没有长期捕获边框；最终稳定成本与释放数据见本轮计划。初次对比脚本未写入而错误归为DXGI的12.33秒/328.1ms读数已撤回，不作证据或性能优劣结论。

允许以下窄例外：AcquireNextFrame等待不能取消，使用不超过2秒有限阻塞，以核对取消/会话条件，绝不零超时忙扫；优先1秒阻塞等待，有帧时分析不超过4次/秒（处理后至少等待250ms），鼠标专用帧也不得造成高频循环。处理间持有当前帧，下一次Acquire前再Release，减少OS反复复制。此为画面内容检测及可取消性的必要采样，不作为其他Provider固定轮询的许可。没有“画面亮度改变”系统摘要通知，进程/前台/窗口重绘通知不能代表完整合成画面；不以逐像素CPU截图代替。

2026-10-18按用户“明亮页面立即降亮”的反馈，将原有1秒处理间隔缩至250ms；只减少启用采集期间的事件处理节流，不把Acquire超时改为250ms，不在静态画面增加4Hz定时查询。即时规则仍由面积滞回抑制反复切换，原有非零稳定时间保持。该上限不等于端到端250ms保证，实际受捕获、原生接口和调度影响。

静态画面可能因有限等待产生必要取消检查唤醒，开启采集不承诺接近零CPU。时段外、HDR和暂停无采集；相关系统事件订阅及下个时段期限单列。短测创建/稳定CPU、帧计数、动态场景与释放，记录混合负载局限；不外推完整产品/游戏/全部GPU预算通过。

依据：[WGC与HDR格式](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)、[捕获边框权限](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired)、[AcquireNextFrame取消限制](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-acquirenextframe)、[ReleaseFrame性能建议](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-releaseframe)。
