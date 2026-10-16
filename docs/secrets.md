# 密钥管理（Secret Manager）

## 原则
- 第三方供应商的 API Key（`DEEPSEEK_API_KEY`、`MOONSHOT_API_KEY`、`DASHSCOPE_API_KEY`、`ZHIPU_API_KEY`、`OPENROUTER_API_KEY`、`XIAOMI_MIMO_API_KEY`）放在 **Secret Manager**，以引用方式挂载到 Cloud Run，不写入仓库、`appsettings.json`、GitHub 变量、命令行参数或日志。
- Gemini 走 Cloud Run 运行服务账号（ADC），没有密钥。
- Firebase Web API Key（`FIREBASE_API_KEY`）按设计是公开配置，会下发给浏览器，不属于机密，继续作为普通变量。
- 应用读取的仍是同名环境变量（`OpenAICompatibleCatalog` 通过 `ApiKeyEnv` 读取），所以**代码不需要改动**。

## 部署如何处理配置（已修复的问题）
旧的部署命令使用 `--set-env-vars`，它会**替换**服务上的全部环境变量：任何在控制台或其他命令里手工加上的变量（包括供应商 Key）会在下一次部署时消失，对应供应商随之被静默禁用。现在 `scripts/lib/cloud-run-release.sh` 一律使用 `--update-env-vars`（合并）和 `--update-secrets`（合并），并有测试守护（`scripts/test/cloud-run-release.test.sh` 会在出现 `--set-env-vars`/`--set-secrets` 时失败）。

## 一次性设置（你来做；我不会替你创建云端资源）
1. 创建密钥，值通过标准输入传入，避免出现在命令历史里：
   ```bash
   printf '%s' "$VALUE" | gcloud secrets create deepseek-api-key --project=$PROJECT_ID --data-file=-
   ```
   轮换时用 `gcloud secrets versions add deepseek-api-key --data-file=-`。
2. 只给运行服务账号授予**这个密钥**的读取权限，不要授予项目级角色：
   ```bash
   gcloud secrets add-iam-policy-binding deepseek-api-key --project=$PROJECT_ID \
     --member="serviceAccount:<CLOUD_RUN_SERVICE_ACCOUNT>" --role=roles/secretmanager.secretAccessor
   ```
3. 如果该变量之前是明文环境变量，先**一次性**移除，避免"同名变量类型冲突"（具体报错文案以 gcloud 实际输出为准）：
   ```bash
   gcloud run services update vertex-ai-api --project=$PROJECT_ID --region=$REGION --remove-env-vars=DEEPSEEK_API_KEY
   ```
   注意这一步会产生一个没有该 Key 的新修订版，请紧接着执行第 4 步；也可以在低峰期操作。
4. 设置 GitHub 仓库变量 `API_SECRETS`（或在命令行导出同名变量运行 `scripts/deploy-cloud-run.sh`），格式 `ENV_NAME=secret-name:version`，逗号分隔：
   ```text
   API_SECRETS=DEEPSEEK_API_KEY=deepseek-api-key:3,MOONSHOT_API_KEY=moonshot-api-key:latest
   ```
   - 为空时部署**不改动**服务上已有的密钥挂载。
   - 推荐写**明确的版本号**而不是 `latest`：每个修订版固化了自己引用的版本，回滚到旧修订版时会连同旧密钥版本一起回去；`latest` 在实例启动时解析，轮换后需要新修订版/新实例才会生效。

## 回滚
部署脚本在冒烟失败时自动把流量切回上一个修订版（见 `docs/cloud-run.md`）。手工回滚：
```bash
gcloud run services update-traffic vertex-ai-api  --project=$PROJECT_ID --region=$REGION --to-revisions=<上一个修订版>=100
gcloud run services update-traffic vertex-ai-web  --project=$PROJECT_ID --region=$REGION --to-revisions=<上一个修订版>=100
```
修订版名称可用 `gcloud run revisions list --service=vertex-ai-api --region=$REGION` 查看。

## 核对（只读）
```bash
gcloud run services describe vertex-ai-api --project=$PROJECT_ID --region=$REGION \
  --format='yaml(spec.template.spec.containers[0].env)'
```
- 供应商 Key 应出现为 `valueFrom.secretKeyRef`，而不是 `value`。
- 输出里不应出现任何 Key 的明文。
