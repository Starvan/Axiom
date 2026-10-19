# 当前交接

更新：2026-10-19。仓库公开；开始开发按AGENTS设置固定提交身份。

## 仓库维护

- 所有者授权本次历史作者修复：所有可写分支/标签的错误邮箱归到Starvan；代码文件、提交说明和合并结构逐一核对一致。见[身份规则与迁移说明](../development/commit-identity-repair.md)、[旧新SHA对照](../history/commit-identity-map-20261019.tsv)。
- 重新clone后再开发；旧clone/工作树不可直接push、merge或cherry-pick回旧历史。旧SHA在历史文档、CI和二进制BUILD-INFO中保留原事实，按对照表查等价新提交。
- 既有Release资产保留原字节和摘要；本轮不新发版、不重新构建。GitHub管理的PR旧引用/旧活动和统计缓存不等于当前分支仍使用错误邮箱。

## 当前开发状态

- 本轮codex/fractional-screen-stability：画面变化稳定时间支持0–60秒、最多两位小数（如0.2/0.5），旧整数/零秒兼容；配置、250毫秒进入/退出期限及保存导入等6项相关验证通过。只迁入文件补丁，不引回旧历史；用户现有规则数值不改。统一构建及本机目录/三角色来源/后台连接核对通过；原规则指纹保持，未替用户修改稳定时间数值。

- main仍是已发布亮度渐变批次，以下PR保持开放，没有借本轮维护合并功能。
- [PR28](https://github.com/Starvan/Axiom/pull/28)：响应式亮度渐变及多键组合+滚轮；[PR29](https://github.com/Starvan/Axiom/pull/29)叠于其上，新增任务外层HDR/时段自动暂停与事件推送。
- [PR30](https://github.com/Starvan/Axiom/pull/30)：工作台视觉/导航和紧凑任务行，基于PR29；浅深色/窄窗及8项呈现测试此前通过。
- [PR31](https://github.com/Starvan/Axiom/pull/31)基于PR29修复晚启动托盘等待；[PR32](https://github.com/Starvan/Axiom/pull/32)叠于其上修复无关观察重建误取消启动动作。分支名保持。

## 本机与验收边界

- 新clone固定artifacts/Axiom-preview/Axiom.exe已更新local.20261019.3，干净源码685703b。原规则/托盘/登录启动保持，启动位置随新clone更新；滚轮仍关闭且手动接管已解除。验收证据只存本机artifacts/fractional-live.json及tray-startup-verify.json，不提交配置或日志。此前启动恢复修复沿原8fba30a批次证据查。
- 首次开机窗口任务资格已被旧版消耗，未重置，真实下次开机仍待确认。用户后续配置以实时状态为准，不按历史摘要回填。
- 亮度恢复沿原目标/最初基线，Unknown/外部变化/取消仍让出；暂停释放无消费者采集。已有采集、亮度与UI证据沿各PR/计划查，不重复无关验证，不宣称广设备/游戏/完整性能达标。
- 本轮仅核对Git对象/文件树/身份、分支标签和文档链接；旧SHA成功CI不冒充新SHA验证。重新开发时只验证本批功能与直接影响边界。
- 共用用户数据，原地保存，不备份/EFS/打包私有副本；正常停止拒绝或恢复未知时保留现场，不强杀。
