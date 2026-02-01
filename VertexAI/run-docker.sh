#!/bin/bash

# Gemini Chat Docker Compose 一键部署脚本
# 用法: ./run-docker.sh [--down] [--logs]

GCP_KEY_PATH="${GCP_KEY_PATH:-./GCPKey/copper-affinity-467409-k7-9f51b539bf0f.json}"
PROJECT_ID="${PROJECT_ID:-copper-affinity-467409-k7}"
DB_PASSWORD="${DB_PASSWORD:-GeminiChat2024!}"

export GCP_KEY_PATH PROJECT_ID DB_PASSWORD

# 检查 GCP 密钥
if [ ! -f "$GCP_KEY_PATH" ]; then
    echo "[ERROR] 找不到 GCP 密钥文件: $GCP_KEY_PATH"
    exit 1
fi

# 处理命令参数
case "$1" in
    --down|-d)
        echo "[INFO] 停止所有服务..."
        docker compose down
        exit 0
        ;;
    --logs|-l)
        docker compose logs -f
        exit 0
        ;;
    --rebuild|-r)
        echo "[INFO] 重新构建并启动..."
        docker compose down
        docker compose up --build -d
        ;;
    *)
        echo "[INFO] 启动服务..."
        docker compose up --build -d
        ;;
esac

echo ""
echo "========================================="
echo " Gemini Chat 已启动"
echo "========================================="
echo " 访问: http://localhost:8880"
echo ""
echo " 查看日志:  ./run-docker.sh --logs"
echo " 停止服务:  ./run-docker.sh --down"
echo "========================================="
