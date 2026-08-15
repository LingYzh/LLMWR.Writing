# WP11 Full IPC v1 实现计划

> 本计划服从：Product FROZEN > Architecture FROZEN > Implementation Design > ADR > 本文件。
> 不修改 Frozen 文档。不实现 WP12/WP13/WP22。不引入迁移或新依赖。

## 已锁定的合约语义

1. **semanticType**：信封必填稳定字符串；`messageType` 只区分 request/response/event/control。未知 semanticType → `IPC_UNSUPPORTED_SEMANTIC_TYPE` 后断开。禁止按 payload 形状猜测。
2. **事件序列**：每个 Core 进程一个 `eventStreamId`。普通事件 `seq` 从 **1** 起单调递增。`snapshotSeq` 含该快照已覆盖的最大 seq（无事件则为 0）。恢复订阅 `afterSeq` 为 **开区间**：只投递 `seq > afterSeq`。
3. **GapEvent.fromSeq/toSeq**：被丢弃普通事件的 **闭区间**。GapEvent 本身不占用 seq。重复溢出合并为一个 gap；订阅进入 `NeedsResync`，在 snapshot/resync 前不得把后续普通事件当成完整流。
4. **环**：每进程共享 ring 容量 **恰好 256**。慢订阅者不得阻塞 Authority。
5. **队列**：critical outbound 64（响应/控制/心跳/取消，满则 fail-closed，不静默丢）；snapshot outbound 8；in-flight request 32。事件不走无界队列。
6. **RunSession TTL**：DefaultTtl=1h，MaximumTtl=8h。`ExpiresAtMs` 仅为调用方上限；`actual = min(requested or now+Default, now+Maximum)`。调用方不能用巨大时间戳换更长会话。
7. **可信绑定**：`AuthenticatedChannelContext` 只来自 Core 拥有的 `ITrustedIpcBindingRegistry`。无 launch record 则 CreateRunSession **fail-closed**。禁止把 envelope/Hello 的 run/worker/project/role 当授权真相。
8. **Bootstrap**：同进程首次 Hello 成功后轮换内存 secret，经 HelloAck 下发；每端点最多一条已认证连接。Core 重启后环境 token 重新生效。客户端不得自动重放业务 mutation。
9. **取消**：best-effort；COMMIT 后不得声称 rollback。
10. **CF-001** 保持 OPEN / WP22。
