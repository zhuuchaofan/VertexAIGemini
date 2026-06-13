#!/bin/bash

# 球球布丁工作室 Docker Compose 一键部署脚本
# 用法: ./run-docker.sh [--down] [--logs]

GCP_KEY_PATH="${GCP_KEY_PATH:-./GCPKey/credentials.json}"
PROJECT_ID="${PROJECT_ID:-}"
DB_PASSWORD="${DB_PASSWORD:-GeminiChat2024!}"
WEB_PORT="${WEB_PORT:-8880}"

export GCP_KEY_PATH PROJECT_ID DB_PASSWORD WEB_PORT

if [ -z "$PROJECT_ID" ]; then
    echo "[ERROR] 请设置 PROJECT_ID，例如: PROJECT_ID=your-google-cloud-project ./run-docker.sh"
    exit 1
fi

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
echo "       球球布丁工作室 已启动"
echo "========================================="
echo " 访问: http://localhost:${WEB_PORT}"
echo " API 已通过 Web 容器代理到内部 app:8880"
echo ""
echo " 查看日志:  ./run-docker.sh --logs"
echo " 停止服务:  ./run-docker.sh --down"
echo "========================================="
