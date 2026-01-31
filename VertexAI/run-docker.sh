#!/bin/bash

# Gemini Chat Docker 一键部署脚本
# 用法: ./run-docker.sh [--skip-build]
#   默认: 重新构建镜像并运行
#   --skip-build 或 -s: 跳过构建，直接运行

GCP_KEY_PATH="${GCP_KEY_PATH:-./GCPKey/copper-affinity-467409-k7-9f51b539bf0f.json}"
PROJECT_ID="${PROJECT_ID:-copper-affinity-467409-k7}"

# 检查 GCP 密钥
if [ ! -f "$GCP_KEY_PATH" ]; then
    echo "❌ 错误: 找不到 GCP 密钥文件: $GCP_KEY_PATH"
    exit 1
fi

# 停止并删除旧容器（如果存在）
if [ "$(docker ps -aq -f name=gemini-chat)" ]; then
    echo "🗑️  停止旧容器..."
    docker rm -f gemini-chat > /dev/null 2>&1
fi

# 是否跳过构建
if [ "$1" != "--skip-build" ] && [ "$1" != "-s" ]; then
    echo "🔨 构建 Docker 镜像..."
    docker build -t gemini-chat .
    if [ $? -ne 0 ]; then
        echo "❌ 构建失败"
        exit 1
    fi
    echo "✅ 构建完成"
fi

# 构建可选参数
EXTRA_ARGS=""
if [ -n "$SYSTEM_PROMPT" ]; then
    EXTRA_ARGS="-e VertexAI__SystemPrompt=$SYSTEM_PROMPT"
fi

echo ""
echo "🚀 启动 Gemini Chat..."
echo "   访问: http://localhost:8880"
echo ""

docker run -p 8880:8880 \
    -v "$(realpath "$GCP_KEY_PATH")":/app/credentials.json \
    -e GOOGLE_APPLICATION_CREDENTIALS=/app/credentials.json \
    -e VertexAI__ProjectId="$PROJECT_ID" \
    $EXTRA_ARGS \
    --name gemini-chat \
    --rm \
    gemini-chat
