# 生产启用 v2 之前的备份与恢复

状态：**手册，尚未执行任何命令。** 其中每一条会读写云资源的命令都需要你逐项确认后才能运行。
命令参数已对照 `gcloud` 的本地 `--help` 核对过（`firestore export / import`、`databases create / update`、`storage rsync`）。

## 1. 原则

1. **v2 对数据是叠加式的**：只新增可选字段和新集合（`runs`、`userSettings`），不改写、不删除现有文档。所以出问题时的正常回滚是关闭开关或把流量切回上一个修订，**不是恢复数据**。
2. 备份用来防「新代码写错数据」这类意外，例如终结事务误覆盖了消息，或附件被误删。
3. **没有演练过的备份不算备份。** 首次启用前必须完成一次恢复演练（第 5 节），恢复到一个临时数据库，不碰生产。
4. 新的写入路径对附件从不覆盖（见方案 2.3.3：对象创建带「不存在才写」前提），但**删除会话**仍会按现有逻辑删除附件对象，这类误删只能靠附件桶的备份找回。

## 2. 备份范围

| 对象 | 内容 | 备份方式 |
|---|---|---|
| Firestore | `conversations`、`messages`、`usageQuotas`；启用 v2 之后再加上 `runs`、`userSettings` | 托管导出到 Cloud Storage |
| 附件桶 | `gs://<ATTACHMENT_STORAGE_BUCKET>`（脚本默认名为 `<项目>-vertex-ai-attachments`） | 复制到备份桶 |
| 不在范围 | Firebase Auth 用户、Secret Manager 的密钥、Cloud Run 配置 | 本次不改动，不需要 |

## 3. 前置条件（先确认，不要猜）

```bash
export FIRESTORE_PROJECT_ID=<Firestore 所在项目>      # 注意：可能与运行 Cloud Run 的项目不同
export ATTACHMENT_BUCKET=<附件桶名>
export BACKUP_BUCKET=<备份桶名>                        # 例如 ${FIRESTORE_PROJECT_ID}-firestore-backups

# 数据库位置。备份桶必须与数据库在同一位置（区域或多区域）。
gcloud firestore databases describe --project="$FIRESTORE_PROJECT_ID" --database='(default)' \
  --format='value(locationId,type,pointInTimeRecoveryEnablement)'
```

执行备份的账号需要 `roles/datastore.importExportAdmin`，并且对备份桶有写权限；Firestore 的服务代理需要能写该桶。如果权限不足，导出操作会失败并给出明确原因，这时再补权限，不要提前放宽。

## 4. 备份（启用 v2 之前做一次）

```bash
STAMP=$(date -u +%Y%m%dT%H%M%SZ)

# 4.1 备份桶：统一桶级访问，与数据库同一位置。已存在则跳过。
gcloud storage buckets create "gs://$BACKUP_BUCKET" \
  --project="$FIRESTORE_PROJECT_ID" --location=<与数据库相同的位置> --uniform-bucket-level-access

# 一次备份只有一个根：$ROOT/firestore 是 Firestore 导出，$ROOT/attachments 是附件副本。
# scripts/release-cloud-shell.sh backup 使用同样的布局并把 $ROOT 记在本次发布的状态文件里。
ROOT="gs://$BACKUP_BUCKET/pre-v2-$STAMP"

# 4.2 Firestore 托管导出（整库，包含所有集合）。命令会等到完成。
gcloud firestore export "$ROOT/firestore" \
  --project="$FIRESTORE_PROJECT_ID" --database='(default)'

gcloud storage ls -r "$ROOT/firestore"                                   # 必须看到 *.overall_export_metadata

# 4.3 附件：只复制，不删除目标端任何对象。先 --dry-run 看清楚再执行。
gcloud storage rsync --recursive --dry-run "gs://$ATTACHMENT_BUCKET" "$ROOT/attachments"
gcloud storage rsync --recursive          "gs://$ATTACHMENT_BUCKET" "$ROOT/attachments"
```

可选，作为第二层保护：开启 Firestore 时间点恢复（保留 7 天，会产生额外存储费用）。

```bash
gcloud firestore databases update --project="$FIRESTORE_PROJECT_ID" --database='(default)' --enable-pitr
```

记录下来备查：备份根 `$ROOT`（Firestore 导出在 `$ROOT/firestore`，附件在 `$ROOT/attachments`）和完成时间。用脚本备份时它们已记录在状态文件里。

## 5. 恢复演练（启用前必须通过）

目标：证明备份能还原，并且不影响生产。

`$ROOT` 是备份时记录的根（脚本备份：本次发布状态文件 `~/vai-release/release-*.env` 里的 `BACKUP_ROOT=`；手动备份：第 4 节的 `$ROOT`；忘了时 `gcloud storage ls "gs://$BACKUP_BUCKET/"` 找 `pre-v2-<时间戳>/`），后面所有恢复命令都用它：Firestore 在 `$ROOT/firestore`，附件在 `$ROOT/attachments`。

```bash
# 5.1 建一个临时数据库（费用很低，演练后删除）。
gcloud firestore databases create --project="$FIRESTORE_PROJECT_ID" \
  --database=restore-check --location=<与数据库相同的位置> --type=firestore-native

# 5.2 把备份导入临时数据库，而不是生产库。
gcloud firestore import "$ROOT/firestore" \
  --project="$FIRESTORE_PROJECT_ID" --database=restore-check --async
gcloud firestore operations list --project="$FIRESTORE_PROJECT_ID"      # 等 done: true
```

验证清单（人工在控制台或用只读脚本，全部通过才算演练成功）：

- 每个集合的文档数与导出时一致（`conversations`、`messages`、`usageQuotas`）。
- 抽查至少 5 个会话：标题、模型、`updatedAt` 一致；消息条数一致；其中至少一个带附件的会话，其附件对象在备份桶副本里存在、大小一致。
- 抽查一条带 `thinkingContent` 的消息和一条中文长内容的消息，文本逐字一致。

演练结束后删除临时数据库（**需要确认**，确保删的是 `restore-check` 而不是 `(default)`）：

```bash
gcloud firestore databases delete --project="$FIRESTORE_PROJECT_ID" --database=restore-check
```

## 6. 真正需要恢复时

按影响由小到大选，**能用前一种就不用后一种**。

| 场景 | 做法 |
|---|---|
| v2 行为有问题，但数据没坏 | 关闭 `Runs__Enabled`，或把 Cloud Run 流量切回上一个修订。**不恢复数据**。叠加字段和 `runs` 集合保留，对旧代码无害 |
| 少数文档被误删或误改 | 按 5.1、5.2 恢复到临时数据库，再**只把受影响的文档**复制回生产（针对性脚本，需要时再写并先在临时库上试） |
| 附件对象被误删 | 从 `$ROOT/attachments` 复制缺失的对象回附件桶：`gcloud storage rsync --recursive "$ROOT/attachments" "gs://$ATTACHMENT_BUCKET"`，先加 `--dry-run`，**不要带 `--delete-unmatched-destination-objects`** |
| 大面积损坏（最后手段） | 先停止写入（关闭 `Runs__Enabled` 并暂停入口），**先对当前状态再做一次导出**，再对生产库导入备份 |

对生产库直接导入要特别注意：

- 导入会用备份里的版本**覆盖同 ID 的文档**，但**不会删除备份之后新建的文档**。所以备份之后产生的新消息会留在库里，而它所属会话的 `updatedAt`、`tokenCount` 会被回退，数据会出现不一致。
- 这就是它被放在最后的原因，也是演练和针对性恢复更可取的原因。

## 7. 启用 v2 的前置门槛（全部满足才可以把 `Runs__Enabled` 在生产设为 true）

- [ ] 第 3 节的位置、权限已确认，备份桶已创建。
- [ ] 第 4 节的 Firestore 导出已完成，`overall_export_metadata` 存在，已记录前缀与 operation。
- [ ] 附件已复制到备份桶，抽样核对过对象数和大小。
- [ ] 第 5 节恢复演练通过，验证清单全部勾选，临时数据库已删除。
- [ ] 演练过一次「关闭 `Runs__Enabled`」，并核对过 `rollback` 命令要回到的旧修订名（API 新修订部署后立即承接生产流量，所以 API 的回滚路径必须事先演练过），确认旧界面立即可用。
- [ ] 你已经明确确认可以启用。

## 8. 费用与清理

- 导出文件、附件副本和（如启用）PITR 都会产生存储费用，量级取决于数据量；你的是个人使用，预计很小，但请以账单为准。
- 备份桶建议设置生命周期（例如 90 天后删除旧备份），具体保留期由你决定。
- 临时数据库用完即删。
