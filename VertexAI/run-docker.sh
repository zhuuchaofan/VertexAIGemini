#!/bin/bash

# Gemini Chat Docker 启动脚本

GCP_KEY_PATH="${GCP_KEY_PATH:-./GCPKey/copper-affinity-467409-k7-9f51b539bf0f.json}"
PROJECT_ID="${PROJECT_ID:-copper-affinity-467409-k7}"

if [ ! -f "$GCP_KEY_PATH" ]; then
    echo "错误: 找不到 GCP 密钥文件: $GCP_KEY_PATH"
    exit 1
fi

# 构建可选参数
EXTRA_ARGS=""
if [ -n "$SYSTEM_PROMPT" ]; then
    EXTRA_ARGS="-e VertexAI__SystemPrompt=$SYSTEM_PROMPT"
fi

docker run -p 8880:8880 \
    -v "$(realpath "$GCP_KEY_PATH")":/app/credentials.json \
    -e GOOGLE_APPLICATION_CREDENTIALS=/app/credentials.json \
    -e VertexAI__ProjectId="$PROJECT_ID" \
    $EXTRA_ARGS \
    --name gemini-chat \
    --rm \
    gemini-chat
